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

        /// <summary>
        /// How many times this job has already been auto-re-queued after being killed (cancelled) or
        /// failing. 0 for a fresh request. Bounded by <see cref="Configuration.PluginConfiguration.JobMaxRetries"/>
        /// so a permanently-failing item is eventually given up on instead of looping forever. Persisted
        /// to queue.json and restored, so the retry budget survives a restart. (whisper-subs-1t0.)
        /// </summary>
        public int RetryCount { get; init; }
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

        /// <summary>
        /// Persisted retry counter (whisper-subs-1t0). A queue.json written before this feature has no
        /// field, so it deserializes to 0 — the same "fresh job" state a new entry carries, which is
        /// exactly right for a legacy restore.
        /// </summary>
        public int RetryCount { get; set; }
    }

    /// <summary>
    /// The v2 (whisper-subs-1t0) on-disk shape of queue.json: a top-level object carrying BOTH the
    /// pending lanes and the currently in-flight leases, so an item that was dequeued-and-running is no
    /// longer lost on a restart. A pre-v2 queue.json is a bare <see cref="QueueEntry"/> array (no wrapper)
    /// and is still read via the legacy fallback in <see cref="SubtitleQueueService.ParseQueueFile"/>.
    /// </summary>
    public class QueueFile
    {
        /// <summary>Schema version. 2 = this pending+in-flight object shape; a bare array is legacy v1.</summary>
        public int Version { get; set; }

        /// <summary>Items still waiting in the priority lanes, in dequeue order.</summary>
        public List<QueueEntry> Pending { get; set; } = new List<QueueEntry>();

        /// <summary>Leases that were dequeued and running when the snapshot was taken. On restore these
        /// are re-enqueued as Pending (they were interrupted, not completed — redo them).</summary>
        public List<QueueEntry> InFlight { get; set; } = new List<QueueEntry>();
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
        // queued set so a re-request while an item is mid-transcription does not double-queue it. The
        // value is the full work item (whisper-subs-1t0) so an in-flight lease's identity/tier/language/
        // force/retry-count is recoverable — PersistQueue writes it to queue.json, and RestoreQueue
        // re-enqueues it as Pending after an interrupting restart, so a running item is never dropped.
        // Nullable value: the low-level TryReserve(key) identity-only overload (tests / bare reservation)
        // stores null, which PersistQueue skips; every real dispatch reserves WITH the item, so a value
        // is never null in production.
        private readonly ConcurrentDictionary<string, SubtitleWorkItem?> _inFlight = new();

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

        /// <summary>
        /// A live snapshot of the current worker pool for the admin status panel (v4.0), or an empty list
        /// when no pool has been built yet (nothing has dispatched since startup). Thin accessor over the
        /// unit-tested <see cref="WorkerPool.Snapshot"/>.
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "Thin accessor over the unit-tested WorkerPool.Snapshot; depends on live pool state")]
        public IReadOnlyList<WorkerStatus> SnapshotWorkers()
        {
            lock (_poolGate)
            {
                return _pool?.Snapshot() ?? (IReadOnlyList<WorkerStatus>)System.Array.Empty<WorkerStatus>();
            }
        }

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

        /// <summary>
        /// The waiting ("inbound") queue for the admin panel, in the exact order it will run (strongest tier
        /// first, then FIFO): each item's name, tier and language. Capped at <paramref name="max"/> so a
        /// library-wide Generate-All doesn't return thousands of names to a polled endpoint — the full total
        /// is <see cref="PriorityCount"/>. (v4.0.)
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "Reads BaseItem.Name off queued items; the lane ordering it projects is unit-tested in PriorityLanesTests")]
        public IReadOnlyList<(string Name, PriorityTier Tier, string Language)> PendingItems(int max = 200)
            => _lanes.Snapshot()
                     .Take(max < 0 ? 0 : max)
                     .Select(e => (e.Value.Item.Name, (PriorityTier)e.Tier, e.Value.Language))
                     .ToList();

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
                Tier = PriorityScheduling.Stronger(existing.Tier, incoming.Tier),
                // Keep the LARGER retry count so a concurrent fresh request (RetryCount 0) can never reset
                // the retry budget of an item that is already being retried — the bound only ever tightens.
                RetryCount = System.Math.Max(existing.RetryCount, incoming.RetryCount)
            };

        // Reserve a key as in-flight (being processed), retaining the work item so the lease is persistable
        // and re-queueable on an interrupted restart; returns false if already reserved.
        internal bool TryReserve(string key, SubtitleWorkItem? item) => _inFlight.TryAdd(key, item);

        // Identity-only reservation (no retained work item) — used by the low-level dedup tests and any
        // caller that only needs to claim the identity. Stores null, which PersistQueue skips.
        internal bool TryReserve(string key) => TryReserve(key, null);

        // Release after processing (or on failure/cancel) so the same work can be requested again later.
        internal void Release(string key) => _inFlight.TryRemove(key, out _);

        /// <summary>
        /// Pure retry decision (whisper-subs-1t0): an item that has already been retried
        /// <paramref name="retryCount"/> times may be retried again only while it is strictly under the
        /// configured cap. <paramref name="maxRetries"/> &lt;= 0 disables retry entirely (0 = the pre-feature
        /// "drop a killed/failed job" behaviour), so auto-retry is opt-out-able.
        /// </summary>
        internal static bool ShouldRetry(int retryCount, int maxRetries) => retryCount < maxRetries;

        /// <summary>
        /// Cancel/failure transition for an in-flight item. Under <see cref="_dispatchGate"/> — the SAME
        /// lock <see cref="Enqueue"/> and the dequeue+reserve take — it always leaves the in-flight set,
        /// and, when the item still has retry budget (<see cref="ShouldRetry"/>), atomically re-adds it to
        /// the lanes at its original tier with RetryCount+1. This is the exact reverse of
        /// <see cref="TryDequeuePriority"/>'s dequeue+reserve: doing the release and the lane-add under one
        /// gate means the identity is never simultaneously in-flight AND queued, and a concurrent
        /// re-request merges (via <see cref="MergeWork"/>) rather than duplicating. Returns true if the
        /// item was re-queued, false if the retry budget was spent and it was dropped. Persists either way
        /// so the transition is durable.
        /// </summary>
        internal bool RetryOrRelease(SubtitleWorkItem wi, int maxRetries)
        {
            var key = IdentityKey(wi.Item.Id, wi.Language);
            lock (_dispatchGate)
            {
                var requeue = ShouldRetry(wi.RetryCount, maxRetries);
                Release(key);
                if (requeue)
                {
                    _lanes.Enqueue(key, (int)wi.Tier, new SubtitleWorkItem
                    {
                        Item = wi.Item,
                        Language = wi.Language,
                        Completion = null,   // any awaited completion was already signalled by the caller
                        Force = wi.Force,
                        Tier = wi.Tier,
                        RetryCount = wi.RetryCount + 1
                    }, MergeWork);
                }
                PersistQueue();
                return requeue;
            }
        }

        /// <summary>
        /// Terminally drop an in-flight identity that reached a final state (completed successfully, or
        /// given up on after exhausting retries, or unservable by any worker) and persist so queue.json no
        /// longer lists it as in-flight — otherwise a crash would restore a finished item as pending. Under
        /// the gate for a lanes+in-flight snapshot consistent with enqueue/dequeue. The RETRY path never
        /// uses this — it moves the item back to the lanes (see <see cref="RetryOrRelease"/>).
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "Release + PersistQueue (requires Plugin.Instance) — persistence orchestration")]
        private void ReleaseInFlightAndPersist(string key)
        {
            lock (_dispatchGate)
            {
                Release(key);
                PersistQueue();
            }
        }

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
                    // Reserve it in-flight, retaining the work item so the lease is persistable/recoverable.
                    // Honour the result: TryReserve returns false only if the identity is already in-flight,
                    // which under _dispatchGate cannot happen for a freshly-dequeued item — but if it ever
                    // did, drop this copy rather than double-dispatch. queue.json is persisted either way
                    // (the item left the lanes).
                    var reserved = TryReserve(IdentityKey(dequeued.Item.Id, dequeued.Language), dequeued);
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

                // Parse either shape: v2 = { Version, Pending[], InFlight[] }; legacy v1 = a bare
                // QueueEntry[]. The interrupted in-flight leases are restored as Pending too — they were
                // running (not completed) when the snapshot was taken, so they must be redone; an
                // already-satisfied one is a cheap no-op thanks to GenerateSubtitleAsync's existing-file
                // skip (#82). Pending is restored first so, if the same identity appears in both lists
                // (it should not, but be safe), the lane dedup collapses them and we never double-restore.
                var (pending, inFlight) = ParseQueueFile(json);
                var total = pending.Count + inFlight.Count;
                if (total == 0) return 0;

                int restored = 0;
                foreach (var entry in pending.Concat(inFlight))
                {
                    if (!System.Guid.TryParse(entry.ItemId, out var guid)) continue;
                    var item = libraryManager.GetItemById(guid);
                    if (item == null) continue;

                    var tier = PriorityScheduling.NormalizeRestoredTier(entry.Tier);
                    var key = IdentityKey(item.Id, entry.Language);

                    // A queue.json with the same (item, language) more than once must not re-run every
                    // copy — de-dup the restored set too, keeping the strongest tier / OR'd force / larger
                    // retry count. The retry budget carries over so a chronically-failing item is not given
                    // a fresh set of attempts on every restart.
                    var outcome = _lanes.Enqueue(key, (int)tier, new SubtitleWorkItem
                    {
                        Item = item,
                        Language = entry.Language,
                        Completion = null,
                        Force = entry.Force,
                        Tier = tier,
                        RetryCount = entry.RetryCount
                    }, MergeWork);

                    if (outcome == LaneEnqueueOutcome.Added) restored++;
                }

                logger.LogInformation(
                    "[Queue] Restored {Count} items from disk ({Pending} pending + {InFlight} interrupted in-flight re-queued, of {Total} saved)",
                    restored, pending.Count, inFlight.Count, total);
                return restored;
            }
            catch (System.Exception ex)
            {
                logger.LogWarning(ex, "[Queue] Failed to restore queue from {Path}", path);
                return 0;
            }
        }

        /// <summary>
        /// Parse queue.json in either shape (whisper-subs-1t0). v2 is a top-level object
        /// <c>{ Version, Pending[], InFlight[] }</c>; legacy v1 is a bare <see cref="QueueEntry"/> array
        /// (no wrapper) whose entries are all pending. Disambiguated by the first non-whitespace character
        /// (<c>[</c> = array = v1). Pure (string in, two lists out) so the version dispatch and the legacy
        /// fallback are unit-testable without Plugin.Instance / a library. An empty document yields two
        /// empty lists; a malformed one throws <see cref="JsonException"/> (caught + logged by the caller,
        /// preserving the pre-existing "warn on a corrupt queue.json" behaviour).
        /// </summary>
        internal static (List<QueueEntry> Pending, List<QueueEntry> InFlight) ParseQueueFile(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return (new List<QueueEntry>(), new List<QueueEntry>());

            if (json.TrimStart().StartsWith('['))
            {
                var legacy = JsonSerializer.Deserialize<List<QueueEntry>>(json) ?? new List<QueueEntry>();
                return (legacy, new List<QueueEntry>());
            }

            var file = JsonSerializer.Deserialize<QueueFile>(json);
            return file == null
                ? (new List<QueueEntry>(), new List<QueueEntry>())
                : (file.Pending ?? new List<QueueEntry>(), file.InFlight ?? new List<QueueEntry>());
        }

        [ExcludeFromCodeCoverage(Justification = "Requires Plugin.Instance for file path")]
        private void PersistQueue()
        {
            var path = QueueFilePath;
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                // Persist BOTH the pending lanes AND the in-flight leases (whisper-subs-1t0) so a job that
                // was dequeued-and-running survives a restart instead of being silently dropped. Pending
                // tier comes from the lane (authoritative for position); in-flight tier from the work item.
                var pending = _lanes.Snapshot().Select(e => new QueueEntry
                {
                    ItemId = e.Value.Item.Id.ToString("N"),
                    Language = e.Value.Language,
                    Force = e.Value.Force,
                    Tier = e.Tier,
                    RetryCount = e.Value.RetryCount
                }).ToList();

                var inFlight = _inFlight.Values
                    .Where(w => w != null)
                    .Select(w => new QueueEntry
                    {
                        ItemId = w!.Item.Id.ToString("N"),
                        Language = w.Language,
                        Force = w.Force,
                        Tier = (int)w.Tier,
                        RetryCount = w.RetryCount
                    }).ToList();

                var json = JsonSerializer.Serialize(new QueueFile { Version = 2, Pending = pending, InFlight = inFlight });
                lock (_fileLock)
                {
                    // Atomic write (v4.0.1): serialize to a unique temp file then File.Move(overwrite) so a
                    // process kill / disk-full mid-write can't leave a truncated queue.json that fails to
                    // deserialize on restart and silently drops the whole persisted queue. Mirrors the
                    // temp+rename already used by SubtitleRequestStore and SubtitleSkipCache.
                    var tmp = path + "." + System.Guid.NewGuid().ToString("N") + ".tmp";
                    try
                    {
                        File.WriteAllText(tmp, json);
                        File.Move(tmp, path, overwrite: true);
                    }
                    finally
                    {
                        if (File.Exists(tmp))
                        {
                            try { File.Delete(tmp); } catch { /* best effort cleanup */ }
                        }
                    }
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
                _ = Task.Run(async () =>
                {
                    var started = false;
                    try
                    {
                        // Build the pool + requirements INSIDE the try (v4.0.1): GetPool → WorkerRegistry →
                        // SubtitleProviderFactory.CreateLocal parses user-configured model/VAD paths and CAN
                        // throw (e.g. an invalid path → ArgumentException). If that threw before the finally
                        // was in scope, _isDraining stayed 1 forever and wedged the background dispatcher until
                        // a Jellyfin restart. Now a build failure is caught, _isDraining is reset, and — since
                        // the config is broken — we do NOT re-fire (started stays false), avoiding a busy-loop.
                        var pool = GetPool(config, loggerFactory, forTask: false);
                        var requirements = WorkerJob.Requirements(config.SubtitleMode, config.EnableTranslation);
                        started = true;
                        await DispatchDrainAsync(manager, pool, requirements, countProcessed: true, config.JobMaxRetries, logger, cancellationToken);
                    }
                    catch (System.Exception ex)
                    {
                        logger.LogError(ex, "[Dispatch] Dispatcher failed to start or run");
                    }
                    finally
                    {
                        _currentItemName = null;
                        Interlocked.Exchange(ref _isDraining, 0);

                        // Re-check: if items were enqueued during the finally block, restart the loop to avoid
                        // stuck items — but never on an already-cancelled token, and never if the pool build
                        // itself failed (a persistent bad-config throw must not busy-loop). (#112, v4.0.1)
                        if (started && !_lanes.IsEmpty && !cancellationToken.IsCancellationRequested)
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
            int maxRetries,
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
                    // Deterministic fail-fast (broken config), NOT a transient kill — do not retry, just
                    // drop it. Release AND persist so the interrupted-in-flight snapshot on disk no longer
                    // lists it (otherwise it would restore as pending and re-fail every startup).
                    ReleaseInFlightAndPersist(IdentityKey(unservable.Item.Id, unservable.Language));
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
                    pool.SetCurrent(l.Key, wi.Item.Name);   // "what's running where" — surfaced in the status panel
                    logger.LogInformation("[Dispatch] Processing {ItemName} [{Tier}] on {Worker} ({Remaining} remaining)",
                        wi.Item.Name, wi.Tier, l.Worker.Name, _lanes.Count);

                    // Fire the job WITHOUT gating Task.Run on the token: a cancelled token must not skip the
                    // delegate, or the finally (which frees the slot + reservation) would never run and leak
                    // the slot. Cancellation is observed INSIDE, via the token passed to the transcription.
                    running.Add(Task.Run(async () =>
                    {
                        var key = IdentityKey(wi.Item.Id, wi.Language);
                        try
                        {
                            await manager.GenerateSubtitleAsync(
                                wi.Item, l.Worker.Provider, wi.Language, cancellationToken, wi.Force);
                            if (countProcessed) Interlocked.Increment(ref _processedCount);
                            wi.Completion?.TrySetResult(true);
                            // Completed — release the in-flight lease and persist so a crash can't restore
                            // a finished item as pending. (whisper-subs-1t0.)
                            ReleaseInFlightAndPersist(key);
                        }
                        catch (System.OperationCanceledException)
                        {
                            // Cancelled = task stopped or Jellyfin restart mid-transcription. This is the
                            // very case that used to silently drop an item (the #1 bug). Re-queue it for a
                            // bounded retry instead of losing it; RetryOrRelease moves it lanes⇄in-flight
                            // atomically under the gate (never both, never neither).
                            wi.Completion?.TrySetCanceled();
                            if (RetryOrRelease(wi, maxRetries))
                                logger.LogWarning("[Dispatch] {ItemName} was cancelled (task stopped / restart) — re-queued to retry (retry {Retry} of {Max})",
                                    wi.Item.Name, wi.RetryCount + 1, maxRetries);
                            else
                                logger.LogWarning("[Dispatch] Giving up on {ItemName} after {Attempts} attempt(s) — cancelled and out of retries",
                                    wi.Item.Name, wi.RetryCount + 1);
                        }
                        catch (System.Exception ex)
                        {
                            if (countProcessed) Interlocked.Increment(ref _processedCount);
                            Interlocked.Increment(ref _failedCount);
                            _lastError = $"{wi.Item.Name}: {ex.Message}";
                            wi.Completion?.TrySetException(ex);
                            logger.LogError(ex, "[Dispatch] Failed: {ItemName}", wi.Item.Name);
                            // Transient failure (whisper crash, unreachable worker, …) — bounded auto-retry.
                            if (RetryOrRelease(wi, maxRetries))
                                logger.LogWarning("[Dispatch] {ItemName} failed — re-queued to retry (retry {Retry} of {Max})",
                                    wi.Item.Name, wi.RetryCount + 1, maxRetries);
                            else
                                logger.LogWarning("[Dispatch] Giving up on {ItemName} after {Attempts} attempt(s) — it keeps failing",
                                    wi.Item.Name, wi.RetryCount + 1);
                        }
                        finally
                        {
                            // Only the worker SLOT is freed here (always). The in-flight (item,language)
                            // reservation is released along the success/retry/drop paths above — NOT here —
                            // so a re-queued item that was already re-dequeued+reserved by another slot is
                            // not wrongly un-reserved (which would let it dispatch twice).
                            pool.Release(l.Key, wi.Item.Name);
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
            int maxRetries,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            // countProcessed:false — the old priority drain did not add to _processedCount (only the
            // background loop did), so the /Queue `processed` stat stays byte-identical to pre-v4.
            await DispatchDrainAsync(manager, pool, requirements, countProcessed: false, maxRetries, logger, cancellationToken);
            _currentItemName = null;
        }
    }
}
