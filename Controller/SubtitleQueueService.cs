using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WhisperSubs.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace WhisperSubs.Controller
{
    public class SubtitleWorkItem
    {
        public required BaseItem Item { get; init; }
        public required string Language { get; init; }
        public TaskCompletionSource<bool>? Completion { get; init; }

        /// <summary>
        /// True for explicit manual requests: bypasses the "skip if a usable subtitle already
        /// exists" checks (#82) so the user always gets fresh generation. Persisted to disk, so a
        /// forced request survives a restart; scheduled items default to false.
        /// </summary>
        public bool Force { get; init; }

        /// <summary>
        /// Priority tier assigned by the server from the requester's role (#112). Drives dequeue order
        /// — the queue serves the strongest tier first. Every enqueue path sets this explicitly; the
        /// default is only a safe fallback.
        /// </summary>
        public PriorityTier Tier { get; init; } = PriorityTier.Medium;
    }

    public class QueueEntry
    {
        public string ItemId { get; set; } = "";
        public string Language { get; set; } = "";

        /// <summary>Whether this was a forced (manual) request — preserved across restarts so an
        /// explicit "regenerate" survives a restore instead of silently respecting the skip checks.</summary>
        public bool Force { get; set; }

        /// <summary>
        /// Persisted priority tier (#112). Nullable so a queue.json written before this feature (no
        /// tier field) deserializes to null and is normalised to <see cref="PriorityTier.High"/> on
        /// restore — NOT Critical(0), which a non-nullable int default would wrongly imply.
        /// </summary>
        public int? Tier { get; set; }
    }

    public class SubtitleQueueService
    {
        // Lazy<T> so concurrent first-time callers all get the same instance (thread-safe init).
        private static readonly System.Lazy<SubtitleQueueService> _lazy = new(() => new SubtitleQueueService());
        public static SubtitleQueueService Instance => _lazy.Value;

        // Multi-lane priority queue (#112): one FIFO lane per tier, strongest tier drained first.
        // Replaces the former single ConcurrentQueue. De-dup identity is (item, language) — force and
        // tier are merged onto the existing entry rather than queued twice.
        private readonly PriorityLanes<SubtitleWorkItem> _lanes = new();

        // Keys currently being PROCESSED (dequeued, transcription running). Kept separate from the
        // queued set so a re-request while an item is mid-transcription does not double-queue it.
        private readonly ConcurrentDictionary<string, byte> _inFlight = new();

        private int _isDraining;
        private string? _currentItemName;
        private int _processedCount;
        private int _failedCount;
        private string? _lastError;
        private static readonly object _fileLock = new();

        /// <summary>
        /// Global lock — only one whisper transcription at a time.
        /// Must be acquired by both the drain loop and the scheduled task.
        /// </summary>
        public static readonly SemaphoreSlim TranscriptionLock = new(1, 1);

        // ── Scheduled task progress tracking ─────────────────────
        private string? _taskCurrentItemName;
        private int _taskTotal;
        private int _taskProcessed;
        private int _taskFailed;
        private int _taskIsRunning;

        public int PriorityCount => _lanes.Count;
        public string? CurrentItemName => _currentItemName ?? _taskCurrentItemName;
        public bool IsDraining => _isDraining == 1;
        public int ProcessedCount => _processedCount;

        /// <summary>Number of manual-queue items that failed (whisper error, missing binary, etc.).</summary>
        public int FailedCount => _failedCount;

        /// <summary>Last error message from a failed manual-queue item, for surfacing in the UI.</summary>
        public string? LastError => _lastError;

        /// <summary>Queued item counts by named tier (#112) — for the admin queue view.</summary>
        public Dictionary<PriorityTier, int> CountsByTier()
            => _lanes.CountsByTier().ToDictionary(kv => (PriorityTier)kv.Key, kv => kv.Value);

        // ── Per-file progress (updated by WhisperProvider stderr) ──
        private int _currentFileProgress;

        /// <summary>Current file transcription progress (0-100), parsed from whisper stderr.</summary>
        public int CurrentFileProgress => _currentFileProgress;

        /// <summary>Updates the current file's transcription progress.</summary>
        public void ReportFileProgress(int percent)
        {
            Interlocked.Exchange(ref _currentFileProgress, System.Math.Clamp(percent, 0, 100));
        }

        /// <summary>Resets file progress to 0 (call when starting a new item).</summary>
        public void ResetFileProgress()
        {
            Interlocked.Exchange(ref _currentFileProgress, 0);
        }

        /// <summary>Whether the scheduled auto-generation task is running.</summary>
        public bool IsTaskRunning => _taskIsRunning == 1;
        private string? _taskCurrentItemType;
        private string? _taskCurrentItemLibrary;
        private string? _currentPhase;

        public string? TaskCurrentItemName => _taskCurrentItemName;
        public string? TaskCurrentItemType => _taskCurrentItemType;
        public string? TaskCurrentItemLibrary => _taskCurrentItemLibrary;
        public string? CurrentPhase => _currentPhase;
        public int TaskTotal => _taskTotal;
        public int TaskProcessed => _taskProcessed;
        public int TaskFailed => _taskFailed;

        /// <summary>Reports progress from the scheduled task so the Queue endpoint can expose it.</summary>
        public void ReportTaskProgress(string? itemName, int processed, int total, int failed,
            string? itemType = null, string? libraryName = null)
        {
            _taskCurrentItemName = itemName;
            _taskProcessed = processed;
            _taskTotal = total;
            _taskFailed = failed;
            _taskCurrentItemType = itemType;
            _taskCurrentItemLibrary = libraryName;
            if (string.IsNullOrEmpty(itemName))
            {
                _currentPhase = null;
            }
            Interlocked.CompareExchange(ref _taskIsRunning, 1, 0);
        }

        /// <summary>Reports the current processing phase (e.g. "Extracting audio", "Transcribing").</summary>
        public void ReportPhase(string phase)
        {
            _currentPhase = phase;
        }

        /// <summary>Marks the scheduled task as complete.</summary>
        public void ReportTaskComplete()
        {
            _taskCurrentItemName = null;
            _taskCurrentItemType = null;
            _taskCurrentItemLibrary = null;
            _currentPhase = null;
            Interlocked.Exchange(ref _taskIsRunning, 0);
        }

        // De-dup identity for a queued unit of work: same item + language collapses to one entry,
        // regardless of force or tier (#112). Force is OR-merged and tier promoted onto the existing
        // entry, so a user request at Medium followed by an admin request at Critical becomes one
        // Critical, forced job — never two competing jobs. Language is lowercased so "EN"/"en" match.
        internal static string IdentityKey(System.Guid itemId, string language) =>
            $"{itemId:N}|{(language ?? string.Empty).ToLowerInvariant()}";

        // Merge two work items for the same identity: keep the item/language, OR the force flag, promote
        // to the stronger tier, and keep whichever completion source exists (an awaited priority request).
        // Internal (not private) so the merge logic is directly unit-testable.
        internal static SubtitleWorkItem MergeWork(SubtitleWorkItem existing, SubtitleWorkItem incoming) =>
            new SubtitleWorkItem
            {
                Item = existing.Item,
                Language = existing.Language,
                Completion = existing.Completion ?? incoming.Completion,
                Force = existing.Force || incoming.Force,
                Tier = PriorityScheduling.Stronger(existing.Tier, incoming.Tier)
            };

        // Reserve a key as in-flight (being processed); returns false if already reserved.
        internal bool TryReserve(string key) => _inFlight.TryAdd(key, 0);

        // Release after processing (or on failure/cancel) so the same work can be requested again later.
        internal void Release(string key) => _inFlight.TryRemove(key, out _);

        [ExcludeFromCodeCoverage(Justification = "Requires Plugin.Instance")]
        private static string QueueFilePath
        {
            get
            {
                var pluginDir = Plugin.Instance?.DataFolderPath;
                if (string.IsNullOrEmpty(pluginDir)) return "";
                Directory.CreateDirectory(pluginDir);
                return Path.Combine(pluginDir, "queue.json");
            }
        }

        [ExcludeFromCodeCoverage(Justification = "Requires BaseItem + Plugin.Instance for persistence")]
        public bool Enqueue(BaseItem item, string language, PriorityTier tier = PriorityTier.High, bool force = false)
        {
            var key = IdentityKey(item.Id, language);

            // If the same unit is mid-transcription, don't queue a duplicate — the running pass covers it.
            if (_inFlight.ContainsKey(key)) return false;

            var outcome = _lanes.Enqueue(key, (int)tier, new SubtitleWorkItem
            {
                Item = item,
                Language = language,
                Completion = null,
                Force = force,
                Tier = tier
            }, MergeWork);

            // Always persist: even a Duplicate outcome can have OR-merged Force or promoted the tier onto
            // the existing entry via MergeWork, so queue.json must be rewritten to survive a restart —
            // otherwise an explicit forced re-request could silently revert to non-forced on restore (#112).
            PersistQueue();
            return outcome == LaneEnqueueOutcome.Added;
        }

        [ExcludeFromCodeCoverage(Justification = "Requires Plugin.Instance for persistence")]
        public bool TryDequeuePriority(out SubtitleWorkItem? item)
        {
            if (_lanes.TryDequeue(out var dequeued) && dequeued != null)
            {
                // Mark in-flight so a concurrent re-request doesn't double-queue while it transcribes.
                TryReserve(IdentityKey(dequeued.Item.Id, dequeued.Language));
                PersistQueue();
                item = dequeued;
                return true;
            }
            item = null;
            return false;
        }

        /// <summary>
        /// Restores queue from disk on startup. Call after Jellyfin library is available.
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "Requires Plugin.Instance + ILibraryManager")]
        public int RestoreQueue(ILibraryManager libraryManager, ILogger logger)
        {
            var path = QueueFilePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return 0;

            try
            {
                var json = File.ReadAllText(path);
                var entries = JsonSerializer.Deserialize<List<QueueEntry>>(json);
                if (entries == null || entries.Count == 0) return 0;

                int restored = 0;
                foreach (var entry in entries)
                {
                    if (!System.Guid.TryParse(entry.ItemId, out var guid)) continue;
                    var item = libraryManager.GetItemById(guid);
                    if (item == null) continue;

                    var tier = PriorityScheduling.NormalizeRestoredTier(entry.Tier);
                    var key = IdentityKey(item.Id, entry.Language);

                    // A queue.json with the same (item, language) more than once must not re-run every
                    // copy — de-dup the restored set too, keeping the strongest tier / OR'd force.
                    var outcome = _lanes.Enqueue(key, (int)tier, new SubtitleWorkItem
                    {
                        Item = item,
                        Language = entry.Language,
                        Completion = null,
                        Force = entry.Force,
                        Tier = tier
                    }, MergeWork);

                    if (outcome == LaneEnqueueOutcome.Added) restored++;
                }

                logger.LogInformation("[Queue] Restored {Count} items from disk (of {Total} saved)", restored, entries.Count);
                return restored;
            }
            catch (System.Exception ex)
            {
                logger.LogWarning(ex, "[Queue] Failed to restore queue from {Path}", path);
                return 0;
            }
        }

        [ExcludeFromCodeCoverage(Justification = "Requires Plugin.Instance for file path")]
        private void PersistQueue()
        {
            var path = QueueFilePath;
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                var entries = _lanes.Snapshot().Select(e => new QueueEntry
                {
                    ItemId = e.Value.Item.Id.ToString("N"),
                    Language = e.Value.Language,
                    Force = e.Value.Force,
                    Tier = e.Tier
                }).ToList();

                var json = JsonSerializer.Serialize(entries);
                lock (_fileLock)
                {
                    File.WriteAllText(path, json);
                }
            }
            catch
            {
                // Non-critical — best effort persistence
            }
        }

        /// <summary>
        /// Starts the background drain loop if not already running.
        /// Safe to call multiple times — only one drain runs at a time.
        /// Re-checks queue after draining to avoid race with late enqueues.
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "Orchestrates async drain loop with external processes")]
        public void EnsureDraining(
            SubtitleManager manager,
            ISubtitleProvider provider,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _isDraining, 1, 0) == 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await DrainLoopAsync(manager, provider, logger, cancellationToken);
                    }
                    finally
                    {
                        _currentItemName = null;
                        Interlocked.Exchange(ref _isDraining, 0);

                        // Re-check: if items were enqueued during the finally block, restart the drain to
                        // avoid stuck items — but never on an already-cancelled token, which would set
                        // _isDraining=1 while Task.Run skips the delegate, wedging the queue forever (#112).
                        if (!_lanes.IsEmpty && !cancellationToken.IsCancellationRequested)
                        {
                            EnsureDraining(manager, provider, logger, cancellationToken);
                        }
                    }
                }, cancellationToken);
            }
        }

        [ExcludeFromCodeCoverage(Justification = "Orchestrates async drain with external processes")]
        private async Task DrainLoopAsync(
            SubtitleManager manager,
            ISubtitleProvider provider,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            while (TryDequeuePriority(out var workItem) && workItem != null)
            {
                try
                {
                    // Inside the try so the finally still releases the in-flight reservation on cancel (#112).
                    cancellationToken.ThrowIfCancellationRequested();
                    _currentItemName = workItem.Item.Name;
                    logger.LogInformation("[Queue] Processing {ItemName} [{Tier}] ({Remaining} remaining)",
                        workItem.Item.Name, workItem.Tier, _lanes.Count);

                    await TranscriptionLock.WaitAsync(cancellationToken);
                    try
                    {
                        await manager.GenerateSubtitleAsync(
                            workItem.Item, provider, workItem.Language, cancellationToken, workItem.Force);
                    }
                    finally
                    {
                        TranscriptionLock.Release();
                    }

                    Interlocked.Increment(ref _processedCount);
                    workItem.Completion?.TrySetResult(true);
                }
                catch (System.OperationCanceledException)
                {
                    workItem.Completion?.TrySetCanceled();
                    throw;
                }
                catch (System.Exception ex)
                {
                    Interlocked.Increment(ref _processedCount);
                    Interlocked.Increment(ref _failedCount);
                    _lastError = $"{workItem.Item.Name}: {ex.Message}";
                    workItem.Completion?.TrySetException(ex);
                    logger.LogError(ex, "[Queue] Failed: {ItemName}", workItem.Item.Name);
                }
                finally
                {
                    Release(IdentityKey(workItem.Item.Id, workItem.Language));
                }
            }
            logger.LogInformation("[Queue] Drain complete. Processed {Count} items total ({Failed} failed).",
                _processedCount, _failedCount);
        }

        /// <summary>
        /// Process all pending priority items. Called by scheduled task.
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "Orchestrates async drain with external processes")]
        public async Task DrainPriorityAsync(
            SubtitleManager manager,
            ISubtitleProvider provider,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            while (TryDequeuePriority(out var workItem) && workItem != null)
            {
                try
                {
                    // Inside the try so the finally still releases the in-flight reservation on cancel (#112).
                    cancellationToken.ThrowIfCancellationRequested();
                    _currentItemName = workItem.Item.Name;
                    logger.LogInformation("[Priority] Processing {ItemName} [{Tier}]", workItem.Item.Name, workItem.Tier);

                    await TranscriptionLock.WaitAsync(cancellationToken);
                    try
                    {
                        await manager.GenerateSubtitleAsync(
                            workItem.Item, provider, workItem.Language, cancellationToken, workItem.Force);
                    }
                    finally
                    {
                        TranscriptionLock.Release();
                    }

                    workItem.Completion?.TrySetResult(true);
                }
                catch (System.OperationCanceledException)
                {
                    workItem.Completion?.TrySetCanceled();
                    throw;
                }
                catch (System.Exception ex)
                {
                    Interlocked.Increment(ref _failedCount);
                    _lastError = $"{workItem.Item.Name}: {ex.Message}";
                    workItem.Completion?.TrySetException(ex);
                    logger.LogError(ex, "[Priority] Failed: {ItemName}", workItem.Item.Name);
                }
                finally
                {
                    Release(IdentityKey(workItem.Item.Id, workItem.Language));
                }
            }
            _currentItemName = null;
        }
    }
}
