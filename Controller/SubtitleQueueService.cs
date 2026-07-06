using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WhisperSubs.Configuration;
using WhisperSubs.Providers;
using WhisperSubs.Controller.Workers;
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

        // Serialises the queue↔in-flight transition so the invariant "an identity is queued XOR in-flight"
        // holds atomically: the dispatcher's dequeue+reserve and Enqueue's in-flight-check+lane-add run
        // under this one lock. Without it (the two touch _lanes and _inFlight under different locks) a
        // concurrent enqueue in the dequeue→reserve window could re-add an identity that is about to run,
        // letting the pool dispatch the SAME (item,language) twice — two workers writing one .srt (v4.0).
        private readonly object _dispatchGate = new();

        private int _isDraining;
        private string? _currentItemName;
        private int _processedCount;
        private int _failedCount;
        private string? _lastError;
        private static readonly object _fileLock = new();

        // The single shared worker pool (v4.0) — the concurrency gate for ALL transcription, replacing the
        // former global TranscriptionLock(1,1). Built lazily from config and rebuilt (see GetPool) only at a
        // session start when the other consumer is idle and no jobs are in flight, so adding/removing a worker
        // takes effect between drain sessions without ever corrupting a live session's slot accounting. Both
        // the background dispatcher and the scheduled task fetch it via GetPool and converge on this one pool
        // — with the default one local worker of MaxConcurrency 1 it admits exactly one job at a time
        // (byte-identical to the old lock); with N workers it dispatches up to ΣMaxConcurrency concurrently.
        private WorkerPool? _pool;
        private readonly object _poolGate = new();

        /// <summary>
        /// The shared worker pool for the current (or next) drain session, (re)built from config. Rebuilt at a
        /// session start only when the OTHER consumer is idle AND no jobs are in flight, so a rebuild never
        /// splits the concurrency gate (any live holder keeps its captured pool). <paramref name="forTask"/>
        /// excludes the caller's own just-set running flag (the scheduled task vs the background dispatcher).
        /// Picks up config changes (added worker, changed model path) on the next idle session — matching the
        /// old per-call provider construction, without staleness, and without over-rebuilding (once per session).
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "Builds providers from config via WorkerRegistry (Plugin.Instance) — orchestration")]
        internal WorkerPool GetPool(PluginConfiguration config, ILoggerFactory loggerFactory, bool forTask)
        {
            lock (_poolGate)
            {
                var otherConsumerIdle = forTask ? _isDraining == 0 : _taskIsRunning == 0;
                if (_pool == null || (otherConsumerIdle && _pool.ActiveJobs == 0))
                {
                    _pool = new WorkerPool(WorkerRegistry.BuildWorkers(config, loggerFactory));
                }
                return _pool;
            }
        }

        /// <summary>
        /// Marks the scheduled task as running so <see cref="GetPool"/> will not rebuild the shared pool
        /// underneath it during its startup window (before the first progress report). Idempotent.
        /// </summary>
        public void MarkTaskStarted() => Interlocked.CompareExchange(ref _taskIsRunning, 1, 0);

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

            // Under _dispatchGate so the in-flight check and the lane-add are atomic with the dispatcher's
            // dequeue+reserve — otherwise a re-add landing in the dequeue→reserve window double-dispatches.
            lock (_dispatchGate)
            {
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
        }

        [ExcludeFromCodeCoverage(Justification = "Requires Plugin.Instance for persistence")]
        public bool TryDequeuePriority(out SubtitleWorkItem? item)
        {
            // Dequeue + reserve atomically (see _dispatchGate): moving an identity from the lanes to the
            // in-flight set in one critical section is what guarantees the pool never dispatches the same
            // (item,language) twice.
            lock (_dispatchGate)
            {
                if (_lanes.TryDequeue(out var dequeued) && dequeued != null)
                {
                    // Reserve it in-flight. Honour the result: TryReserve returns false only if the identity
                    // is already in-flight, which under _dispatchGate cannot happen for a freshly-dequeued
                    // item — but if it ever did, drop this copy rather than double-dispatch. queue.json is
                    // persisted either way (the item left the lanes).
                    var reserved = TryReserve(IdentityKey(dequeued.Item.Id, dequeued.Language));
                    PersistQueue();
                    if (reserved)
                    {
                        item = dequeued;
                        return true;
                    }
                }
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
        /// Starts the background dispatch loop if not already running (only one at a time), building the
        /// shared worker pool from config. Safe to call multiple times. Re-checks the queue after draining
        /// to avoid a race with late enqueues. Replaces the former single-worker EnsureDraining: with the
        /// default one local worker it behaves identically (one job at a time); with N configured workers it
        /// dispatches up to ΣMaxConcurrency jobs concurrently across the pool.
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "Orchestrates the async dispatch loop with external processes")]
        public void EnsureDispatching(
            SubtitleManager manager,
            PluginConfiguration config,
            ILoggerFactory loggerFactory,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _isDraining, 1, 0) == 0)
            {
                var pool = GetPool(config, loggerFactory, forTask: false);
                var requirements = WorkerJob.Requirements(config.SubtitleMode, config.EnableTranslation);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await DispatchDrainAsync(manager, pool, requirements, countProcessed: true, logger, cancellationToken);
                    }
                    finally
                    {
                        _currentItemName = null;
                        Interlocked.Exchange(ref _isDraining, 0);

                        // Re-check: if items were enqueued during the finally block, restart the loop to
                        // avoid stuck items — but never on an already-cancelled token, which would set
                        // _isDraining=1 while Task.Run skips the delegate, wedging the queue forever (#112).
                        if (!_lanes.IsEmpty && !cancellationToken.IsCancellationRequested)
                        {
                            EnsureDispatching(manager, config, loggerFactory, logger, cancellationToken);
                        }
                    }
                    // Task.Run is deliberately NOT given the cancellation token: if it were and the token were
                    // already cancelled, the delegate would be skipped and the finally above would never reset
                    // _isDraining — wedging the queue forever. Cancellation is observed INSIDE via the captured
                    // token (DispatchDrainAsync checks it), so the finally always runs. (Matches the per-job
                    // Task.Run, which is un-tokened for the same reason.)
                });
            }
        }

        /// <summary>
        /// The core N-slot dispatcher: pulls the highest-priority item, waits for a free worker slot
        /// (backpressure at ΣMaxConcurrency), routes it to the cheapest capable worker, and runs it
        /// concurrently — up to the pool's capacity in flight at once. On completion or failure both the
        /// worker slot and the in-flight (item,language) reservation are released. Shared by the background
        /// loop (fire-and-forget via EnsureDispatching) and the scheduled task's priority drain (awaited via
        /// DrainPriorityAsync); both use the SAME pool so the global concurrency limit always holds. At one
        /// worker of MaxConcurrency 1 the slot serialises exactly like the old TranscriptionLock.
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "Orchestrates concurrent async transcription with external processes")]
        private async Task DispatchDrainAsync(
            SubtitleManager manager,
            WorkerPool pool,
            JobRequirements requirements,
            bool countProcessed,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            // The job requirements are uniform across a drain session, so feasibility is decided once: if no
            // worker can EVER serve them (e.g. translation enabled but every configured worker is
            // transcribe-only) fail the whole queue fast rather than block a slot forever. A misconfig then
            // surfaces loudly (every item errors with a clear message) instead of the queue hanging.
            if (!pool.HasCapableWorker(requirements))
            {
                while (TryDequeuePriority(out var unservable) && unservable != null)
                {
                    // Match the old counting: the background loop counted a failure toward `processed`,
                    // the scheduled task's priority drain did not (countProcessed distinguishes them).
                    if (countProcessed) Interlocked.Increment(ref _processedCount);
                    Interlocked.Increment(ref _failedCount);
                    _lastError = $"{unservable.Item.Name}: no configured worker can serve this job";
                    unservable.Completion?.TrySetException(
                        new System.InvalidOperationException("No configured worker can serve this job"));
                    Release(IdentityKey(unservable.Item.Id, unservable.Language));
                    logger.LogError("[Dispatch] No capable worker for {ItemName} — skipping", unservable.Item.Name);
                }
                _currentItemName = null;
                return;
            }

            var running = new List<Task>();
            try
            {
                while (!_lanes.IsEmpty)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Acquire a free slot FIRST (backpressure at ΣMaxConcurrency), THEN dequeue the current
                    // highest-priority item — so an item leaves the persisted queue only when a worker is
                    // ready to run it now (same crash-durability as the old single-lock loop), and each pick
                    // sees the freshest priority state.
                    var lease = await pool.AcquireAsync(requirements, cancellationToken);
                    if (!TryDequeuePriority(out var workItem) || workItem == null)
                    {
                        // Emptied by a concurrent consumer between the check and the dequeue — release, re-check.
                        pool.Release(lease.Key);
                        continue;
                    }

                    var wi = workItem;
                    var l = lease;
                    _currentItemName = wi.Item.Name;
                    logger.LogInformation("[Dispatch] Processing {ItemName} [{Tier}] on {Worker} ({Remaining} remaining)",
                        wi.Item.Name, wi.Tier, l.Worker.Name, _lanes.Count);

                    // Fire the job WITHOUT gating Task.Run on the token: a cancelled token must not skip the
                    // delegate, or the finally (which frees the slot + reservation) would never run and leak
                    // the slot. Cancellation is observed INSIDE, via the token passed to the transcription.
                    running.Add(Task.Run(async () =>
                    {
                        try
                        {
                            await manager.GenerateSubtitleAsync(
                                wi.Item, l.Worker.Provider, wi.Language, cancellationToken, wi.Force);
                            if (countProcessed) Interlocked.Increment(ref _processedCount);
                            wi.Completion?.TrySetResult(true);
                        }
                        catch (System.OperationCanceledException)
                        {
                            wi.Completion?.TrySetCanceled();
                        }
                        catch (System.Exception ex)
                        {
                            if (countProcessed) Interlocked.Increment(ref _processedCount);
                            Interlocked.Increment(ref _failedCount);
                            _lastError = $"{wi.Item.Name}: {ex.Message}";
                            wi.Completion?.TrySetException(ex);
                            logger.LogError(ex, "[Dispatch] Failed: {ItemName}", wi.Item.Name);
                        }
                        finally
                        {
                            Release(IdentityKey(wi.Item.Id, wi.Language));
                            pool.Release(l.Key);
                        }
                    }));

                    // Reap finished tasks so the list can't grow unbounded on a long backlog.
                    running.RemoveAll(t => t.IsCompleted);
                }
            }
            finally
            {
                // Wait for every dispatched job to finish before returning, so the caller (and _isDraining)
                // only sees "drained" once the pool is truly idle. Individual job errors are handled above.
                try { await Task.WhenAll(running); } catch { /* per-job exceptions already handled */ }
            }

            logger.LogInformation("[Dispatch] Drain complete. Processed {Count} items total ({Failed} failed).",
                _processedCount, _failedCount);
        }

        /// <summary>
        /// Processes all currently-queued priority items across the worker pool and awaits their completion.
        /// Called by the scheduled task to clear manual/user requests (which outrank the background sweep)
        /// before and between its own items. Uses the SAME shared pool as the background dispatcher, so the
        /// two together never exceed the global concurrency limit. With the default one local worker this is
        /// a sequential drain (identical to the old single-lock behaviour); with N workers it runs in parallel.
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "Orchestrates concurrent async transcription with external processes")]
        internal async Task DrainPriorityAsync(
            SubtitleManager manager,
            WorkerPool pool,
            JobRequirements requirements,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            // countProcessed:false — the old priority drain did not add to _processedCount (only the
            // background loop did), so the /Queue `processed` stat stays byte-identical to pre-v4.
            await DispatchDrainAsync(manager, pool, requirements, countProcessed: false, logger, cancellationToken);
            _currentItemName = null;
        }
    }
}
