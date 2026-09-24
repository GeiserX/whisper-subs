using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WhisperSubs.Configuration;
using WhisperSubs.Providers;
using WhisperSubs.Controller.Workers;
using WhisperSubs.Setup;
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

        /// <summary>
        /// Null for a normal generate job. For a translate job: the one target ("en" or a Canary code),
        /// already validated and lower-case. Part of the queue identity, so a translate job can sit next to
        /// a generate job for the same item, and next to a translate job for another target.
        /// </summary>
        public string? Target { get; init; }
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

        /// <summary>
        /// The translate job's target. Absent in every queue.json written before translate jobs existed and
        /// for every normal job, so a legacy entry restores as a normal job and a normal job's JSON is
        /// byte-identical to what 4.9.0.1 wrote.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Target { get; set; }
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

        // Added to the launch-probe wait before a paused drain retries, so the retry lands just PAST the
        // probe window. Landing exactly on it would race the cache and be served the stale verdict. (#185.)
        private static readonly System.TimeSpan ResumeProbeMargin = System.TimeSpan.FromSeconds(1);

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

        // The worker "signature" the current _pool was built or last reconciled from (whisper-subs-9gq): a
        // stable string over the CONFIGURED workers (composition plan + each row's routing-key / url / model /
        // concurrency / cost / translate). ReconcileWorkers skips the provider-constructing rebuild when this
        // is unchanged, so an unrelated config save — e.g. toggling PauseOnPlayback — does not needlessly news
        // up RemoteWhisperProvider/HttpClient instances. Guarded by _poolGate, alongside _pool.
        private string? _workersSignature;

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
                    _pool = new WorkerPool(
                        WorkerRegistry.BuildWorkers(config, loggerFactory),
                        loggerFactory.CreateLogger<WorkerPool>());
                    // Keep the reconcile signature in step with the config the live pool was just built from,
                    // so a subsequent unchanged config save is a no-op in ReconcileWorkers. (whisper-subs-9gq.)
                    _workersSignature = ComputeWorkersSignature(config);
                }
                return _pool;
            }
        }

        /// <summary>
        /// The live pool, or null when nothing has built one yet. Read-only: unlike <see cref="GetPool"/> it
        /// never builds or rebuilds, so a controller can ask it between two jobs of a running drain without
        /// swapping the pool under the dispatcher.
        /// </summary>
        internal WorkerPool? CurrentPool { get { lock (_poolGate) return _pool; } }

        /// <summary>
        /// Why no engine can make <paramref name="target"/> on this server right now, or null when one can
        /// (always null for "en", which Whisper makes). Asks the live pool when there is one, otherwise what
        /// the settings would build. The wording is the one a translate job fails with, so refusing up front
        /// (HTTP 409) and failing later read the same.
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "Reads the live pool and the filesystem; the rules are the unit-tested TranslationRoute.EngineMissingReason and WorkerTargets.ServedByConfig")]
        public string? TargetEngineUnavailableReason(PluginConfiguration config, string target)
        {
            if (string.Equals(target, "en", System.StringComparison.OrdinalIgnoreCase)) return null;
            var installed = SubtitleProviderFactory.IsCanaryInstalled(config.CrispAsrBinaryPath, config.CanaryModelPath, File.Exists);
            var rows = config.Workers ?? new List<WhisperWorker>();
            var usable = rows.Count(w => w.Enabled && !string.IsNullOrWhiteSpace(w.ApiUrl));
            var legacy = !string.IsNullOrWhiteSpace(config.RemoteWhisperApiUrl);
            var hostsLocal = WorkerPlan.HostsLocal(rows.Count, usable, legacy, config.EnableLocalWorker);
            var pool = CurrentPool;
            var available = pool != null
                ? pool.HasCapableWorker(WorkerJob.ForTarget(target))
                : WorkerTargets.ServedByConfig(target, installed, hostsLocal, rows);
            // Without a pool, what the pool WOULD hold (hostsLocal), not null: null maps to the
            // "not installed" wording, which is false for an installed engine with the local worker off.
            var state = TranslationRoute.LocalCanary(installed, pool?.HasLocalWorker ?? hostsLocal,
                rows.Count, usable, legacy, config.EnableLocalWorker);
            return TranslationRoute.EngineMissingReason(target, available, state);
        }

        /// <summary>
        /// Hot-applies a Workers-config change to the LIVE pool without a Jellyfin restart (whisper-subs-9gq).
        /// Under <see cref="_poolGate"/> (the lock guarding <see cref="_pool"/>): when a pool exists and the
        /// configured workers actually changed, it rebuilds the desired worker set from <paramref name="config"/>
        /// and GROWS the pool via <see cref="WorkerPool.Reconcile"/> so a just-added worker joins the running
        /// drain immediately — without disturbing in-flight jobs on the other workers, and preserving the
        /// sole-dispatch invariant (Reconcile mutates the pool under the pool's own gate). When no pool exists
        /// yet this is a no-op: the next <see cref="GetPool"/> builds fresh from the new config anyway. A cheap
        /// workers-signature comparison skips the (provider-constructing) rebuild when the worker set is
        /// unchanged, so an unrelated config save does not churn HttpClients. Returns the live worker count.
        /// </summary>
        /// <remarks>
        /// GROW-ONLY (see <see cref="WorkerPool.Reconcile"/>): a newly-added worker joins the live drain
        /// immediately, but REMOVING a worker — or EDITING the URL/Id of an existing one — takes effect only on
        /// the next idle <see cref="GetPool"/> rebuild or a restart. Editing the URL of a blank-Id worker
        /// re-keys it, so both the old and edited worker run until that rebuild. The added / removed workers are
        /// logged by <see cref="WorkerPool.Reconcile"/>.
        /// </remarks>
        [ExcludeFromCodeCoverage(Justification = "Builds providers from config via WorkerRegistry — orchestration; the signature (ComputeWorkersSignature) and WorkerPool.Reconcile are unit-tested")]
        public int ReconcileWorkers(PluginConfiguration config, ILoggerFactory loggerFactory)
        {
            lock (_poolGate)
            {
                if (_pool == null)
                {
                    // Nothing live to grow — GetPool will build fresh from this config on the next drain.
                    return 0;
                }

                var signature = ComputeWorkersSignature(config);
                if (signature == _workersSignature)
                {
                    return _pool.WorkerCount;   // workers unchanged — skip the provider rebuild
                }

                var count = _pool.Reconcile(WorkerRegistry.BuildWorkers(config, loggerFactory));
                _workersSignature = signature;
                return count;
            }
        }

        /// <summary>
        /// A stable signature of the CONFIGURED transcription workers (whisper-subs-9gq): the backward-compat
        /// composition decision (<see cref="WorkerPlan.Decide"/>) plus, per contributing worker, its routing
        /// key, URL, model, MaxConcurrency, cost, translation targets and dialect — everything that changes which
        /// workers the pool would contain or their capacity. <see cref="ReconcileWorkers"/> compares it to
        /// detect whether the worker set actually changed, so an unrelated config save is a cheap no-op.
        /// Mirrors <see cref="WorkerRegistry.BuildWorkers"/>' enabled+non-blank filter and key derivation so
        /// the signature tracks the workers the pool would really contain. Pure and internal so it is
        /// unit-testable without a live pool.
        /// </summary>
        internal static string ComputeWorkersSignature(PluginConfiguration config)
        {
            var (source, addLocal) = WorkerPlan.Decide(
                config.Workers?.Count ?? 0,
                !string.IsNullOrWhiteSpace(config.RemoteWhisperApiUrl),
                config.EnableLocalWorker);

            var sb = new StringBuilder();
            sb.Append("src=").Append(source).Append(";local=").Append(addLocal).Append(';');

            if (source == WorkerSource.ExplicitList && config.Workers != null)
            {
                // Collect each contributing row's segment, then append them SORTED (whisper-subs-9gq L1) so that
                // merely reordering worker rows in the config does not flip the signature — which would run a
                // needless no-op provider rebuild. Same fields/format as before; only per-row ordering is stable.
                var segments = new List<string>();
                foreach (var w in config.Workers)
                {
                    if (!w.Enabled || string.IsNullOrWhiteSpace(w.ApiUrl)) continue;
                    var id = string.IsNullOrWhiteSpace(w.Id) ? w.ApiUrl : w.Id;
                    segments.Add(
                        $"w[{id}|{w.ApiUrl.Trim()}|{(w.Model ?? string.Empty).Trim()}|" +
                        $"{(w.MaxConcurrency < 1 ? 1 : w.MaxConcurrency)}|" +
                        $"{w.CostWeight.ToString(System.Globalization.CultureInfo.InvariantCulture)}|" +
                        $"{WorkerTargets.Signature(WorkerTargets.ForRow(w))}|{WorkerDialect.Normalize(w.Dialect)}];");
                }
                foreach (var seg in segments.OrderBy(s => s, System.StringComparer.Ordinal))
                    sb.Append(seg);
            }
            else if (source == WorkerSource.LegacyRemote)
            {
                sb.Append("remote[")
                  .Append((config.RemoteWhisperApiUrl ?? string.Empty).Trim()).Append('|')
                  .Append((config.RemoteWhisperModel ?? string.Empty).Trim()).Append("];");
            }

            return sb.ToString();
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
        public IReadOnlyList<(string Name, PriorityTier Tier, string Language, string? Target)> PendingItems(int max = 200)
            => _lanes.Snapshot()
                     .Take(max < 0 ? 0 : max)
                     .Select(e => (e.Value.Item.Name, (PriorityTier)e.Tier, e.Value.Language, e.Value.Target))
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
        // A translate job adds its target ("|>es"), so it never merges with the generate job for the same
        // item or with a translate job for another target. A normal job's key is unchanged.
        internal static string IdentityKey(System.Guid itemId, string language, string? target = null)
        {
            var key = $"{itemId:N}|{(language ?? string.Empty).ToLowerInvariant()}";
            var t = NormalizeJobTarget(target);
            return t == null ? key : key + "|>" + t;
        }

        /// <summary>The identity of a work item: its item, language and, for a translate job, its target.</summary>
        internal static string IdentityKey(SubtitleWorkItem wi) => IdentityKey(wi.Item.Id, wi.Language, wi.Target);

        /// <summary>A translate job's target, trimmed and lower-case; null for a normal generate job.</summary>
        internal static string? NormalizeJobTarget(string? target)
            => string.IsNullOrWhiteSpace(target) ? null : target.Trim().ToLowerInvariant();

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
                RetryCount = System.Math.Max(existing.RetryCount, incoming.RetryCount),
                // Same identity means same target; keep it so a merged translate job stays one.
                Target = existing.Target
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
        /// Whether a persisted lease should be restored to the lanes on startup (whisper-subs-1t0). A Pending
        /// lease never started, so it is ALWAYS restored (its budget is untouched). An IN-FLIGHT lease was
        /// running when the process died — that counts as one consumed attempt, so it is restored only while
        /// it still has retry budget (<see cref="ShouldRetry"/>); an in-flight lease already at/over the cap is
        /// dropped rather than looped forever (the hard-kill boot-loop guard for an item that OOM-kills every
        /// run — restore-unbounded would re-run it at an unchanged count and wedge the whole queue on restart).
        /// </summary>
        internal static bool ShouldRestore(bool wasInFlight, int retryCount, int maxRetries) =>
            !wasInFlight || ShouldRetry(retryCount, maxRetries);

        /// <summary>
        /// The RetryCount a restored lease re-enters the lanes at (whisper-subs-1t0). A Pending lease never
        /// started, so it keeps its count unchanged; an in-flight lease consumed one attempt (its process was
        /// killed mid-run), so it comes back at RetryCount+1 — which, together with <see cref="ShouldRestore"/>,
        /// bounds restarts so a permanently-killed item is eventually given up on instead of boot-looping.
        /// </summary>
        internal static int RestoredRetryCount(bool wasInFlight, int retryCount) =>
            wasInFlight ? retryCount + 1 : retryCount;

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
            var key = IdentityKey(wi);
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
                        RetryCount = wi.RetryCount + 1,
                        Target = wi.Target
                    }, MergeWork);
                }
                PersistQueue();
                return requeue;
            }
        }

        /// <summary>
        /// Re-queue an in-flight item WITHOUT spending a retry, for a failure that is the WORKER's and not
        /// the item's. Issue #185: a local whisper-cli that cannot launch exits instantly for every item, so
        /// charging each one an attempt empties a 315-item queue against a binary that never ran — and the
        /// items are gone once the container is fixed. Same lanes⇄in-flight transition as
        /// <see cref="RetryOrRelease"/>, at the same tier, with <see cref="SubtitleWorkItem.RetryCount"/>
        /// unchanged, and it always re-queues (there is no budget to exhaust). The worker is taken out of
        /// rotation by the caller, so the item cannot immediately land on the same broken worker again.
        /// </summary>
        internal void RequeueWithoutRetry(SubtitleWorkItem wi)
        {
            var key = IdentityKey(wi);
            lock (_dispatchGate)
            {
                Release(key);
                _lanes.Enqueue(key, (int)wi.Tier, new SubtitleWorkItem
                {
                    Item = wi.Item,
                    Language = wi.Language,
                    Completion = null,   // any awaited completion was already signalled by the caller
                    Force = wi.Force,
                    Tier = wi.Tier,
                    RetryCount = wi.RetryCount,
                    Target = wi.Target
                }, MergeWork);
                PersistQueue();
            }
        }

        /// <summary>What the background dispatch loop does once a drain returns.</summary>
        public enum DrainFollowUp
        {
            /// <summary>Nothing left to do: the queue is empty, we are shutting down, or the pool never built.</summary>
            Stop,

            /// <summary>Items arrived while the drain was finishing — go straight round again.</summary>
            ReDispatchNow,

            /// <summary>
            /// The drain paused because no worker could run the items. Wait for the launch probe to go stale,
            /// then try again: the binary may be fixed by then, and re-firing at once would spin at full speed.
            /// </summary>
            ReDispatchAfterProbeWindow
        }

        /// <summary>
        /// What to do after a drain. Pure so the paused case is pinned by a test rather than by watching a
        /// live dispatcher: it must re-fire on a delay (never immediately — that is a busy loop against a
        /// binary that cannot start), and must not re-fire at all on an empty queue or a cancelled token.
        /// Without the delayed re-fire a local-only install would stay parked until the next request or the
        /// daily task, which is up to a day for what may be a one-off failure. (Issue #185.)
        /// </summary>
        internal static DrainFollowUp DecideFollowUp(bool started, bool paused, bool queueEmpty, bool cancelled)
        {
            if (!started || queueEmpty || cancelled) return DrainFollowUp.Stop;
            return paused ? DrainFollowUp.ReDispatchAfterProbeWindow : DrainFollowUp.ReDispatchNow;
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

        /// <param name="target">Null for a normal generate job. For a translate job, the one target to make
        /// ("en" or a Canary code); the caller has already validated it.</param>
        [ExcludeFromCodeCoverage(Justification = "Requires BaseItem + Plugin.Instance for persistence")]
        public bool Enqueue(BaseItem item, string language, PriorityTier tier = PriorityTier.High, bool force = false, string? target = null)
        {
            // De-dup invariant: Enqueue only READS _inFlight; the in-flight reservation happens once,
            // at drain-entry (TryDequeuePriority → TryReserve), and every releaser lives in
            // DispatchDrainAsync. So a failed/never-started drain leaves items in the lanes (pending,
            // persisted), never orphaned in _inFlight — correctness here depends on PersistQueue
            // swallowing (not propagating) its exceptions, which it does.
            var jobTarget = NormalizeJobTarget(target);
            var key = IdentityKey(item.Id, language, jobTarget);

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
                    Tier = tier,
                    Target = jobTarget
                }, MergeWork);

                // Always persist: even a Duplicate outcome can have OR-merged Force or promoted the tier onto
                // the existing entry via MergeWork, so queue.json must be rewritten to survive a restart —
                // otherwise an explicit forced re-request could silently revert to non-forced on restore (#112).
                PersistQueue();
                return outcome == LaneEnqueueOutcome.Added;
            }
        }

        /// <summary>
        /// Queues one translate job per leaf for <paramref name="target"/>, each as <c>(leaf, "auto", target)</c>.
        /// Shared by the admin Translate endpoint and the viewer request path. Returns how many were newly
        /// queued and how many were already queued or running for that target.
        /// </summary>
        internal (int Queued, int Skipped) EnqueueTranslations(
            IEnumerable<BaseItem> leaves, string target, PriorityTier tier, bool force)
        {
            int queued = 0, skipped = 0;
            foreach (var leaf in leaves)
            {
                if (Enqueue(leaf, "auto", tier, force, target)) queued++; else skipped++;
            }
            return (queued, skipped);
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
                    var reserved = TryReserve(IdentityKey(dequeued), dequeued);
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
        /// Restores queue from disk on startup (whisper-subs-1t0). Call after the Jellyfin library is
        /// available. Pending leases (never started) are restored unincremented; an interrupted IN-FLIGHT
        /// lease counts as one consumed attempt, so it is restored at RetryCount+1 while it still has budget
        /// and DROPPED (given up on) once it has hit the retry cap. Without that bound an item whose process
        /// is SIGKILL'd mid-transcription every time (OOM on a long film) would restore at an unchanged
        /// RetryCount, be drained before the sweep, OOM again, restore again — an infinite boot-loop that
        /// wedges ALL subtitle generation on every restart. <paramref name="maxRetries"/> is the same cap the
        /// dispatcher uses (config.JobMaxRetries).
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "Requires Plugin.Instance + ILibraryManager; the restore decision is the unit-tested RestoreFromEntries")]
        public int RestoreQueue(ILibraryManager libraryManager, ILogger logger, int maxRetries)
        {
            var path = QueueFilePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return 0;

            try
            {
                var json = File.ReadAllText(path);

                // Parse either shape: v2 = { Version, Pending[], InFlight[] }; legacy v1 = a bare
                // QueueEntry[] (all pending). An interrupted in-flight lease is redone — but BOUNDED (see
                // RestoreFromEntries): it consumed one attempt, so it comes back at RetryCount+1 and is
                // dropped once out of budget, killing the hard-kill boot-loop.
                var (pending, inFlight) = ParseQueueFile(json);
                if (pending.Count + inFlight.Count == 0) return 0;

                var (restored, givenUp) = RestoreFromEntries(
                    pending, inFlight, maxRetries, guid => libraryManager.GetItemById(guid));

                foreach (var (name, retryCount) in givenUp)
                {
                    logger.LogWarning(
                        "[Queue] Giving up on {Name} after {Attempts} attempt(s) — it was killed mid-transcription and is out of retries; not restoring",
                        name, retryCount + 1);
                }

                logger.LogInformation(
                    "[Queue] Restored {Count} of {Total} saved items ({Pending} pending + {InFlight} interrupted in-flight; {Dropped} in-flight given up at the {Max}-retry cap)",
                    restored, pending.Count + inFlight.Count, pending.Count, inFlight.Count, givenUp.Count, maxRetries);
                return restored;
            }
            catch (System.Exception ex)
            {
                logger.LogWarning(ex, "[Queue] Failed to restore queue from {Path}", path);
                return 0;
            }
        }

        /// <summary>
        /// The restore core (whisper-subs-1t0), separated from <see cref="RestoreQueue"/> so it is
        /// unit-testable without Plugin.Instance / a live ILibraryManager: it depends only on the parsed
        /// entries, the retry cap, and an item resolver. Restores into the lanes and returns the count of
        /// newly-added items PLUS the in-flight leases it GAVE UP on (name + prior RetryCount) for the caller
        /// to log. Rules:
        /// <list type="bullet">
        /// <item>Pending entries never started → always restored at their unchanged RetryCount.</item>
        /// <item>In-flight entries were running when the snapshot was taken → count as one consumed attempt:
        /// restored at RetryCount+1 while <see cref="ShouldRetry"/>, else dropped (the boot-loop guard).</item>
        /// </list>
        /// Pending is processed first so a (should-not-happen) same-identity Pending∩InFlight overlap collapses
        /// onto the pending copy via the lane dedup (<see cref="MergeWork"/>) rather than double-restoring.
        /// Tier-less legacy entries normalise to High; only a genuinely-<see cref="LaneEnqueueOutcome.Added"/>
        /// entry is counted.
        /// </summary>
        internal (int Restored, List<(string Name, int RetryCount)> GivenUp) RestoreFromEntries(
            List<QueueEntry> pending,
            List<QueueEntry> inFlight,
            int maxRetries,
            System.Func<System.Guid, BaseItem?> resolveItem)
        {
            int restored = 0;
            var givenUp = new List<(string Name, int RetryCount)>();

            foreach (var (entry, wasInFlight) in
                     pending.Select(e => (e, false)).Concat(inFlight.Select(e => (e, true))))
            {
                if (!System.Guid.TryParse(entry.ItemId, out var guid)) continue;
                var item = resolveItem(guid);
                if (item == null) continue;

                // An in-flight lease already at/over the cap is given up on — NOT re-enqueued — so a job that
                // is SIGKILL'd mid-transcription every restart (OOM) can't boot-loop the whole queue. Pending
                // leases never started, so ShouldRestore always keeps them (unincremented).
                if (!ShouldRestore(wasInFlight, entry.RetryCount, maxRetries))
                {
                    givenUp.Add((item.Name, entry.RetryCount));
                    continue;
                }

                // queue.json is read from disk and the target ends up in a file name, so it is re-checked
                // against the catalog; an entry naming anything else is dropped.
                var target = NormalizeJobTarget(entry.Target);
                if (target != null && !WorkerTargets.IsValidTarget(target)) continue;

                var tier = PriorityScheduling.NormalizeRestoredTier(entry.Tier);
                var key = IdentityKey(item.Id, entry.Language, target);

                // De-dup the restored set (same (item,language) more than once collapses to one lane entry),
                // keeping the strongest tier / OR'd force / larger retry count via MergeWork.
                var outcome = _lanes.Enqueue(key, (int)tier, new SubtitleWorkItem
                {
                    Item = item,
                    Language = entry.Language,
                    Completion = null,
                    Force = entry.Force,
                    Tier = tier,
                    RetryCount = RestoredRetryCount(wasInFlight, entry.RetryCount),
                    Target = target
                }, MergeWork);

                if (outcome == LaneEnqueueOutcome.Added) restored++;
            }

            return (restored, givenUp);
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

        /// <summary>One work item as it is written to queue.json. Pure.</summary>
        internal static QueueEntry ToEntry(SubtitleWorkItem w, int tier) => new QueueEntry
        {
            ItemId = w.Item.Id.ToString("N"),
            Language = w.Language,
            Force = w.Force,
            Tier = tier,
            RetryCount = w.RetryCount,
            Target = w.Target
        };

        /// <summary>The queue.json text for a snapshot (the v2 object shape). Pure.</summary>
        internal static string SerializeQueueFile(List<QueueEntry> pending, List<QueueEntry> inFlight)
            => JsonSerializer.Serialize(new QueueFile { Version = 2, Pending = pending, InFlight = inFlight });

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
                var pending = _lanes.Snapshot().Select(e => ToEntry(e.Value, e.Tier)).ToList();

                var inFlight = _inFlight.Values
                    .Where(w => w != null)
                    .Select(w => ToEntry(w!, (int)w!.Tier))
                    .ToList();

                var json = SerializeQueueFile(pending, inFlight);
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
                    var paused = false;
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
                        paused = await DispatchDrainAsync(manager, pool, requirements, countProcessed: true, config.JobMaxRetries, logger, cancellationToken);
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
                        // A PAUSED drain adds a third case: re-firing at once would spin against a binary
                        // that cannot start, and never re-firing would strand the queue until the next
                        // request or the daily task. Retry when the launch probe goes stale. (Issue #185.)
                        switch (DecideFollowUp(started, paused, _lanes.IsEmpty, cancellationToken.IsCancellationRequested))
                        {
                            case DrainFollowUp.ReDispatchNow:
                                EnsureDispatching(manager, config, loggerFactory, logger, cancellationToken);
                                break;
                            case DrainFollowUp.ReDispatchAfterProbeWindow:
                                ScheduleResumeAfterProbeWindow(manager, config, loggerFactory, logger, cancellationToken);
                                break;
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
        /// Re-fires the dispatch loop once the cached launch verdict goes stale, so a paused queue resumes
        /// by itself the moment the binary works again. A delayed continuation rather than a background
        /// service: it exists only while a queue is actually parked, it dies with the cancellation token,
        /// and the <see cref="EnsureDispatching"/> re-entry guard collapses any overlap with a drain started
        /// by an incoming request. The extra second past the window is what makes the next
        /// <c>LocalBinaryHealth.Check</c> genuinely re-probe instead of being served the same cached verdict
        /// and pausing again immediately. (Issue #185.)
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "A timed continuation over the unit-tested DecideFollowUp + LocalBinaryHealth.TimeUntilStale")]
        private void ScheduleResumeAfterProbeWindow(
            SubtitleManager manager,
            PluginConfiguration config,
            ILoggerFactory loggerFactory,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            var delay = LocalBinaryHealth.Instance.TimeUntilStale() + ResumeProbeMargin;
            logger.LogInformation("[Dispatch] Re-checking the local engine in {Seconds:F0}s; {Count} item(s) waiting",
                delay.TotalSeconds, _lanes.Count);

            _ = Task.Run(async () =>
            {
                try { await Task.Delay(delay, cancellationToken); }
                catch (System.OperationCanceledException) { return; }

                // Something may have drained the queue (an incoming request re-fired the loop) or the server
                // may be going down — either way there is nothing to resume.
                if (_lanes.IsEmpty || cancellationToken.IsCancellationRequested) return;

                EnsureDispatching(manager, config, loggerFactory, logger, cancellationToken);
            });
        }

        /// <summary>
        /// The core N-slot dispatcher: pulls the highest-priority item, waits for a free worker slot
        /// (backpressure at ΣMaxConcurrency), routes it to the cheapest capable worker, and runs it
        /// concurrently — up to the pool's capacity in flight at once. On completion or failure both the
        /// worker slot and the in-flight (item,language) reservation are released. Shared by the background
        /// loop (fire-and-forget via EnsureDispatching) and the scheduled task's priority drain (awaited via
        /// DrainPriorityAsync); both use the SAME pool so the global concurrency limit always holds. At one
        /// worker of MaxConcurrency 1 the slot serialises exactly like the old TranscriptionLock.
        /// <para>
        /// Returns true when the drain PAUSED: every worker that could run the queued items is out of
        /// rotation (issue #185 — the host's whisper-cli cannot launch), so the items are still queued with
        /// their retry budgets intact and the caller must not immediately re-fire. The same holds when a
        /// translate job was kept back because every worker that makes its target is out of rotation.
        /// </para>
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "Orchestrates concurrent async transcription with external processes")]
        private async Task<bool> DispatchDrainAsync(
            SubtitleManager manager,
            WorkerPool pool,
            JobRequirements requirements,
            bool countProcessed,
            int maxRetries,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            void FailUnservable(SubtitleWorkItem unservable)
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
                ReleaseInFlightAndPersist(IdentityKey(unservable));
                logger.LogError("[Dispatch] No capable worker for {ItemName} — skipping", unservable.Item.Name);
            }

            // The job requirements are uniform across a drain session, so feasibility is decided once: if no
            // worker can EVER serve them (e.g. translation enabled but every configured worker is
            // transcribe-only) fail the queue fast rather than block a slot forever. A misconfig then
            // surfaces loudly (every item errors with a clear message) instead of the queue hanging.
            // Translate jobs are not judged by these requirements: each one runs on a worker that lists its
            // own target, and fails with the target's own reason when there is none. So they stay queued,
            // in their order, and this drain runs them with any worker as their first lease.
            var poolServesItems = pool.HasCapableWorker(requirements);
            if (!poolServesItems)
            {
                var translateJobs = new List<SubtitleWorkItem>();
                while (TryDequeuePriority(out var unservable) && unservable != null)
                {
                    if (unservable.Target != null) translateJobs.Add(unservable);
                    else FailUnservable(unservable);
                }
                foreach (var job in translateJobs) RequeueWithoutRetry(job);
                if (translateJobs.Count == 0)
                {
                    _currentItemName = null;
                    return false;
                }
            }
            var leaseRequirements = WorkerJob.DispatchLease(requirements, poolServesItems);

            // "The binary is on disk" is not "the binary runs": a CUDA build outlives the container it was
            // downloaded in and then exits 127 on every chunk. Settle that ONCE per drain, before anything is
            // dequeued, and park the local worker if it cannot start — otherwise the queue is fed item by
            // item to a process that never launches. The probe is cached (LocalBinaryHealth), so a drain that
            // starts seconds after a status poll costs nothing. (Issue #185.)
            var localLaunchError = LocalBinaryHealth.Instance.Check(() => ProbeLocalBinaryLaunch(logger));
            if (pool.SetLocalAvailability(localLaunchError) && localLaunchError != null)
            {
                // Once per state change, not once per item — hundreds of identical lines was half the report.
                logger.LogError("[Dispatch] Local whisper-cli cannot start — local worker paused: {Error}", localLaunchError);
                _lastError = localLaunchError;
            }

            var running = new List<Task>();
            var paused = false;
            // Translate jobs whose target's only workers are out of rotation (issue #185). Each keeps its
            // retries and is held back until this drain ends, then re-queued: re-queuing at once would put
            // it straight back on a worker that cannot run it, round after round.
            var parked = new ConcurrentQueue<SubtitleWorkItem>();
            try
            {
                while (!_lanes.IsEmpty)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // Acquire a free slot FIRST (backpressure at ΣMaxConcurrency), THEN dequeue the current
                    // highest-priority item — so an item leaves the persisted queue only when a worker is
                    // ready to run it now (same crash-durability as the old single-lock loop), and each pick
                    // sees the freshest priority state.
                    WorkerLease lease;
                    try
                    {
                        lease = await pool.AcquireAsync(leaseRequirements, cancellationToken);
                    }
                    catch (NoAvailableWorkerException ex)
                    {
                        // Nothing was dequeued, so every queued item keeps its place, its tier and its full
                        // retry budget. Stop here instead of failing them against a worker that cannot run.
                        // (Issue #185.)
                        _lastError = ex.Message;
                        logger.LogError("[Dispatch] Paused with {Count} item(s) still queued — {Reason}",
                            _lanes.Count, ex.Message);
                        paused = true;
                        break;
                    }

                    if (!TryDequeuePriority(out var workItem) || workItem == null)
                    {
                        // Emptied by a concurrent consumer between the check and the dequeue — release, re-check.
                        pool.Release(lease.Key);
                        continue;
                    }

                    if (workItem.Target == null && !poolServesItems)
                    {
                        // A generate job queued while this drain runs only translate jobs.
                        pool.Release(lease.Key);
                        FailUnservable(workItem);
                        continue;
                    }

                    var wi = workItem;
                    var l = lease;
                    // A translate job shows its target, so two jobs for one title can be told apart. SetCurrent
                    // and Release below must use this same string.
                    var label = wi.Target == null ? wi.Item.Name : $"{wi.Item.Name} ({wi.Target})";
                    _currentItemName = label;
                    pool.SetCurrent(l.Key, label);   // "what's running where" — surfaced in the status panel
                    logger.LogInformation("[Dispatch] Processing {ItemName} [{Tier}] on {Worker} ({Remaining} remaining)",
                        label, wi.Tier, l.Worker.Name, _lanes.Count);

                    // Fire the job WITHOUT gating Task.Run on the token: a cancelled token must not skip the
                    // delegate, or the finally (which frees the slot + reservation) would never run and leak
                    // the slot. Cancellation is observed INSIDE, via the token passed to the transcription.
                    running.Add(Task.Run(async () =>
                    {
                        var key = IdentityKey(wi);
                        // A translate job may hand its slot back early, to wait for the worker that makes its
                        // target; the finally below must then not release it a second time.
                        var slotReleased = 0;
                        void ReleaseSlot()
                        {
                            if (Interlocked.Exchange(ref slotReleased, 1) == 0) pool.Release(l.Key, label);
                        }
                        try
                        {
                            if (wi.Target != null)
                            {
                                // A translate job: only the translation pass, for its one target.
                                await manager.TranslateSubtitleAsync(
                                    wi.Item, l.Worker.Provider, wi.Target, wi.Force,
                                    new PoolTargetEngines(pool, l, releaseOwnLease: ReleaseSlot), cancellationToken);
                            }
                            else
                            {
                                await manager.GenerateSubtitleAsync(
                                    wi.Item, l.Worker.Provider, wi.Language, cancellationToken, wi.Force,
                                    new PoolTargetEngines(pool, l));
                            }
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
                        catch (NoAvailableWorkerException ex) when (wi.Target != null)
                        {
                            // Every worker that makes this target is out of rotation. Nothing is wrong with the
                            // job, so like a whole item in a paused drain it keeps its retries: it waits for
                            // the next drain, which re-checks the local engine first.
                            _lastError = ex.Message;
                            wi.Completion?.TrySetException(ex);
                            parked.Enqueue(wi);
                            logger.LogWarning("[Dispatch] {ItemName} waits for the next run: no worker that makes '{Target}' is available — {Reason}",
                                wi.Item.Name, wi.Target, ex.Message);
                        }
                        catch (TranslationNotPossibleException ex)
                        {
                            // The audio is not English, no engine serves the target, or the item has no video
                            // file: every attempt would give the same answer. Final, like the unservable
                            // fail-fast above: record it and drop the job, no retry.
                            _lastError = ex.Message;
                            wi.Completion?.TrySetException(ex);
                            if (countProcessed) Interlocked.Increment(ref _processedCount);
                            Interlocked.Increment(ref _failedCount);
                            logger.LogWarning("[Dispatch] {Message}", ex.Message);
                            ReleaseInFlightAndPersist(key);
                        }
                        catch (System.Exception ex) when (l.Worker.Capabilities.IsLocal
                                                          && WhisperLaunchException.Find(ex) != null)
                        {
                            // whisper-cli never started (missing shared library / illegal instruction). That is
                            // the WORKER's fault, identical for every item, so: park the worker, record it for
                            // the setup page, say why exactly once, and put the item back WITHOUT charging it a
                            // retry. Without this, the queue walks the whole library spending every item's
                            // budget on a binary that cannot launch. (Issue #185.)
                            var launch = WhisperLaunchException.Find(ex)!;
                            _lastError = launch.Message;
                            wi.Completion?.TrySetException(ex);
                            LocalBinaryHealth.Instance.RecordLaunchFailure(launch.Message);

                            if (pool.MarkUnavailable(l.Key, launch.Message))
                            {
                                logger.LogError(ex,
                                    "[Dispatch] Local whisper-cli could not start (exit {ExitCode}) — pausing the local worker; {ItemName} and the rest of the queue keep their retries",
                                    launch.ExitCode, wi.Item.Name);
                            }

                            RequeueWithoutRetry(wi);
                        }
                        catch (System.Exception ex)
                        {
                            _lastError = $"{wi.Item.Name}: {ex.Message}";
                            wi.Completion?.TrySetException(ex);
                            logger.LogError(ex, "[Dispatch] Failed: {ItemName}", wi.Item.Name);
                            // Transient failure (whisper crash, unreachable worker, …) — bounded auto-retry.
                            // Count ONLY the terminal outcome: an attempt that is about to be re-queued is not
                            // yet a processed/failed item, so a 4×-retried item must not inflate Processed/Failed
                            // by 4. The give-up branch — where the retry budget is finally spent — is the one
                            // that counts it once as processed+failed (mirrors the fail-fast unservable path).
                            if (RetryOrRelease(wi, maxRetries))
                            {
                                logger.LogWarning("[Dispatch] {ItemName} failed — re-queued to retry (retry {Retry} of {Max})",
                                    wi.Item.Name, wi.RetryCount + 1, maxRetries);
                            }
                            else
                            {
                                if (countProcessed) Interlocked.Increment(ref _processedCount);
                                Interlocked.Increment(ref _failedCount);
                                logger.LogWarning("[Dispatch] Giving up on {ItemName} after {Attempts} attempt(s) — it keeps failing",
                                    wi.Item.Name, wi.RetryCount + 1);
                            }
                        }
                        finally
                        {
                            // Only the worker SLOT is freed here (always). The in-flight (item,language)
                            // reservation is released along the success/retry/drop paths above — NOT here —
                            // so a re-queued item that was already re-dequeued+reserved by another slot is
                            // not wrongly un-reserved (which would let it dispatch twice).
                            ReleaseSlot();
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

                // Every job has finished, so nothing can park another one now.
                if (!parked.IsEmpty)
                {
                    logger.LogWarning("[Dispatch] Paused with {Count} translate job(s) kept for the next run — no worker that makes their language is available",
                        parked.Count);
                    while (parked.TryDequeue(out var held)) RequeueWithoutRetry(held);
                    paused = true;
                }
            }

            logger.LogInformation("[Dispatch] Drain complete. Processed {Count} items total ({Failed} failed).",
                _processedCount, _failedCount);
            return paused;
        }

        /// <summary>
        /// Runs the cached "can the local whisper-cli launch?" probe against this server's data folder.
        /// Null when it starts fine, when nothing is installed, or when there is no plugin instance to
        /// resolve the binary from. (Issue #185.)
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "Spawns the installed binary via WhisperSetupService; the caching rule and the message mapping are unit-tested")]
        private static string? ProbeLocalBinaryLaunch(ILogger logger)
        {
            var dataPath = Plugin.Instance?.DataFolderPath;
            if (string.IsNullOrEmpty(dataPath)) return null;
            return new WhisperSetupService(logger, dataPath).ProbeInstalledBinaryLaunch();
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
            // The pause flag is for the background loop's self-re-fire guard; the scheduled task simply
            // returns and tries again on its next run. (Issue #185.)
            _ = await DispatchDrainAsync(manager, pool, requirements, countProcessed: false, maxRetries, logger, cancellationToken);
            _currentItemName = null;
        }
    }
}
