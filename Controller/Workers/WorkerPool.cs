using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace WhisperSubs.Controller.Workers
{
    /// <summary>
    /// A reserved worker slot handed out by <see cref="WorkerPool.AcquireAsync"/>: the chosen worker plus
    /// the pool-internal <see cref="Key"/> to release it by. Releasing by the key (not the worker's own Id)
    /// keeps slot accounting correct even if two configured workers collide on Id.
    /// </summary>
    internal readonly record struct WorkerLease(string Key, ITranscriptionWorker Worker);

    /// <summary>
    /// The live transcription worker pool (v4.0): the immutable <see cref="ITranscriptionWorker"/>
    /// descriptors plus their mutable in-flight counts, behind one lock, gated by a ΣMaxConcurrency
    /// backpressure semaphore. It replaces the single global <c>TranscriptionLock(1,1)</c> — with the
    /// default one local worker of MaxConcurrency 1 it admits exactly one job at a time (byte-identical to
    /// the old lock); with N workers it admits up to the summed capacity and routes each job to the cheapest
    /// free worker via the pure <see cref="WorkerScheduling"/>. ALL transcription paths (queue drain, user
    /// request, scheduled sweep) share ONE pool, so the global concurrency limit holds no matter which path
    /// started the work — never two whisper runs on a single-slot worker.
    /// </summary>
    internal sealed class WorkerPool
    {
        private readonly object _gate = new();
        private readonly List<string> _keys = new();
        private readonly Dictionary<string, ITranscriptionWorker> _byKey = new();
        private readonly Dictionary<string, int> _inFlight = new();
        // The item name(s) each worker is transcribing right now — surfaced in the status panel so the admin
        // sees "what's running where" (a worker with MaxConcurrency > 1 can hold several). (v4.0.)
        private readonly Dictionary<string, List<string>> _current = new();
        private readonly SemaphoreSlim _slots;
        // Production passes a real logger (SubtitleQueueService.GetPool); the unit tests construct the pool
        // without one (NullLogger). Used for the diagnostic over-release tripwire and hot-add / removal logging.
        private readonly ILogger _logger;

        public WorkerPool(IReadOnlyList<ITranscriptionWorker> workers, ILogger? logger = null)
        {
            if (workers == null) throw new ArgumentNullException(nameof(workers));
            _logger = logger ?? NullLogger.Instance;

            var keys = ComputeKeys(workers);
            var capacity = 0;
            for (var i = 0; i < workers.Count; i++)
            {
                InitWorkerSlot(keys[i], workers[i]);
                capacity += Cap(workers[i]);
            }

            TotalCapacity = capacity < 1 ? 1 : capacity;
            // Single-arg (UNCAPPED) SemaphoreSlim — deliberately NOT the two-arg (max-capped) form: Reconcile
            // grows the live pool via _slots.Release(delta), which the two-arg ctor would reject with
            // SemaphoreFullException once past its initial max. Normal dispatch keeps Acquire/Release strictly
            // 1:1, so it can never over-release past TotalCapacity — only Reconcile ever grows it. (whisper-subs-9gq.)
            _slots = new SemaphoreSlim(TotalCapacity);
        }

        /// <summary>Summed MaxConcurrency across all workers (≥1): how many jobs may run at once. Grows when
        /// <see cref="Reconcile"/> hot-adds a worker to the live pool. (whisper-subs-9gq.)</summary>
        public int TotalCapacity { get; private set; }

        // Initialise one worker's per-slot state (routing key → descriptor + zeroed in-flight / current-items).
        // ONE source of truth shared by the constructor (object not yet published) and Reconcile (under _gate),
        // so a hot-added worker is set up byte-identically to a constructor-time one. (whisper-subs-9gq.)
        private void InitWorkerSlot(string key, ITranscriptionWorker w)
        {
            _keys.Add(key);
            _byKey[key] = w;
            _inFlight[key] = 0;
            _current[key] = new List<string>();
        }

        // One worker's slot contribution: MaxConcurrency floored to 1 (a nonsense concurrency still gets one slot).
        private static int Cap(ITranscriptionWorker w)
            => w.Capabilities.MaxConcurrency < 1 ? 1 : w.Capabilities.MaxConcurrency;

        // Canonical, collision-safe routing keys for a worker list — the SAME scheme the constructor and
        // Reconcile share: the first worker with a given Id keeps its Id; a later duplicate gets "{Id}#{index}"
        // so the reverse key→worker lookup stays unambiguous (the key is internal slot accounting; WorkerSlot.Id
        // still carries it for the deterministic ordinal tiebreak in WorkerScheduling.Pick). Computed against a
        // FRESH seen-set (independent of the live pool) so Reconcile can diff the result against the current
        // keys. (whisper-subs-9gq.)
        private static List<string> ComputeKeys(IReadOnlyList<ITranscriptionWorker> workers)
        {
            var keys = new List<string>(workers.Count);
            var seen = new HashSet<string>();
            for (var i = 0; i < workers.Count; i++)
            {
                var id = workers[i].Id;
                var key = seen.Contains(id) ? $"{id}#{i}" : id;
                keys.Add(key);
                seen.Add(key);
            }
            return keys;
        }

        /// <summary>
        /// Pure grow-only diff (whisper-subs-9gq), factored out of <see cref="Reconcile"/> so the add / keep /
        /// ignore-removal decision is unit-testable without a live pool. Given the pool's current routing keys
        /// and a desired worker list, returns the desired keys NOT yet present (to ADD) and the current keys
        /// absent from desired (REMOVED — reported for observability but intentionally NOT acted on by the
        /// grow-only reconcile). Uses the SAME collision-safe key scheme as the constructor via <see cref="ComputeKeys"/>.
        /// </summary>
        internal static (IReadOnlyList<string> Added, IReadOnlyList<string> Removed) DiffWorkers(
            IReadOnlyCollection<string> currentKeys, IReadOnlyList<ITranscriptionWorker> desired)
        {
            var current = new HashSet<string>(currentKeys);
            var desiredKeys = ComputeKeys(desired);
            var desiredSet = new HashSet<string>(desiredKeys);

            var added = new List<string>();
            foreach (var k in desiredKeys)
                if (!current.Contains(k)) added.Add(k);

            var removed = new List<string>();
            foreach (var k in currentKeys)
                if (!desiredSet.Contains(k)) removed.Add(k);

            return (added, removed);
        }

        /// <summary>Number of workers in the pool.</summary>
        public int WorkerCount => _keys.Count;

        /// <summary>Jobs currently running across all workers (0 when fully idle).</summary>
        public int ActiveJobs
        {
            get
            {
                lock (_gate)
                {
                    var sum = 0;
                    foreach (var n in _inFlight.Values) sum += n;
                    return sum;
                }
            }
        }

        /// <summary>
        /// A live per-worker snapshot for the admin status panel (v4.0): each worker's identity, current
        /// in-flight load, and the static facts the UI shows. Taken under the pool lock so counts are
        /// consistent with each other.
        /// </summary>
        public IReadOnlyList<WorkerStatus> Snapshot()
        {
            lock (_gate)
            {
                var list = new List<WorkerStatus>(_keys.Count);
                foreach (var key in _keys)
                {
                    var w = _byKey[key];
                    var caps = w.Capabilities;
                    list.Add(new WorkerStatus(
                        w.Id, w.Name, _inFlight[key],
                        caps.MaxConcurrency < 1 ? 1 : caps.MaxConcurrency, caps.IsLocal, caps.CostWeight,
                        new List<string>(_current[key])));
                }
                return list;
            }
        }

        /// <summary>
        /// True if ANY worker could serve <paramref name="job"/>'s hard requirements ignoring current load —
        /// i.e. a capable worker exists (maybe busy). The dispatcher checks this before <see cref="AcquireAsync"/>
        /// so a job no worker can EVER serve is failed fast instead of blocking a slot forever.
        /// </summary>
        public bool HasCapableWorker(JobRequirements job)
        {
            lock (_gate)
            {
                foreach (var key in _keys)
                {
                    // InFlight 0 ⇒ CanServe reduces to the pure capability filter (translate/model), no load.
                    if (WorkerScheduling.CanServe(new WorkerSlot(key, true, 0, _byKey[key].Capabilities), job))
                        return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Waits for a free slot (backpressure at <see cref="TotalCapacity"/>), then reserves the cheapest
        /// worker that can serve <paramref name="job"/> and returns a <see cref="WorkerLease"/>. The caller
        /// MUST call <see cref="Release"/> with the lease's key exactly once when the job finishes. Assumes a
        /// capable worker EXISTS (guard with <see cref="HasCapableWorker"/>): if every free slot is on an
        /// incapable worker it hands the slot back and retries after a short backoff until a capable one
        /// frees, so it always returns a lease unless cancelled.
        /// </summary>
        public async Task<WorkerLease> AcquireAsync(JobRequirements job, CancellationToken cancellationToken)
        {
            // Defense-in-depth: the callers already fail-fast on !HasCapableWorker, but if a call site ever
            // skipped that, the retry loop below would spin forever (acquire slot → PickLocked fails →
            // release → delay). Fail deterministically instead so a misuse surfaces as an exception, not a
            // hung job. When a capable worker EXISTS but is busy, this passes and the loop waits for it.
            if (!HasCapableWorker(job))
            {
                throw new System.InvalidOperationException(
                    "No worker in the pool can serve this job's requirements.");
            }

            while (true)
            {
                await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);

                WorkerLease? lease;
                lock (_gate)
                {
                    lease = PickLocked(job);
                }

                if (lease is not null) return lease.Value;

                // A slot freed but only incapable workers are free (heterogeneous-capability case). Give the
                // slot back and wait briefly for the capable-but-busy worker to free — the HasCapableWorker
                // guard guarantees one exists, so this terminates.
                ReleaseSlots();
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Records the item a leased worker is now transcribing, for the "what's running where" status
        /// panel. Call once after <see cref="AcquireAsync"/>, before the transcription runs; pass the same
        /// <paramref name="itemName"/> to <see cref="Release"/> so it is cleared when the job finishes.
        /// </summary>
        public void SetCurrent(string leaseKey, string itemName)
        {
            lock (_gate)
            {
                if (_current.TryGetValue(leaseKey, out var items)) items.Add(itemName);
            }
        }

        /// <summary>
        /// Releases a slot after a job completes or fails, by the <see cref="WorkerLease.Key"/>. Pass the
        /// <paramref name="currentItem"/> given to <see cref="SetCurrent"/> to clear it from the status
        /// panel (omit on paths that acquired a slot but never dispatched an item). Pair each
        /// <see cref="AcquireAsync"/> with exactly one Release.
        /// </summary>
        public void Release(string leaseKey, string? currentItem = null)
        {
            lock (_gate)
            {
                if (currentItem != null && _current.TryGetValue(leaseKey, out var items))
                    items.Remove(currentItem);
                if (_inFlight.TryGetValue(leaseKey, out var n) && n > 0)
                    _inFlight[leaseKey] = n - 1;
            }
            ReleaseSlots();
        }

        /// <summary>
        /// Hot-adds newly-configured workers to the LIVE pool without a restart (whisper-subs-9gq) — the
        /// "add a worker mid-backlog" case (e.g. a just-provisioned Mac mini). GROW-ONLY: every worker in
        /// <paramref name="desired"/> not already present is added (its per-slot state initialised exactly as
        /// the constructor does) and the backpressure semaphore is grown by the added capacity, so the new
        /// slot(s) become dispatchable immediately — even while a drain is in flight. Workers already in the
        /// pool are LEFT UNTOUCHED: their in-flight counts and "what's running" lists survive, so no running
        /// job is disturbed. A worker that DISAPPEARED from the config is deliberately NOT removed here — live
        /// removal needs permit-shrink accounting that is out of scope for this change; a removed worker simply
        /// stops receiving new jobs on the next idle <c>GetPool</c> rebuild. Returns the resulting worker count.
        ///
        /// Concurrency: the map mutation runs under <see cref="_gate"/> — the SAME lock <see cref="PickLocked"/>
        /// and <see cref="Release"/> take — so a dispatcher observes the old-or-new worker set atomically, never
        /// a partial one. The semaphore is grown AFTER the maps are mutated and OUTSIDE the lock, so a waiter
        /// woken by the new permit is guaranteed to see the added worker when it re-enters PickLocked.
        /// </summary>
        /// <remarks>
        /// GROW-ONLY, and the caller must understand the limits: a newly-added worker joins the LIVE pool
        /// immediately, but REMOVING a worker — or EDITING the URL/Id of an existing one — does NOT take effect
        /// here; it applies only on the next idle <c>GetPool</c> rebuild or a Jellyfin restart. In particular,
        /// editing the URL of a worker row whose Id is BLANK re-keys it (the routing key derives from the URL
        /// when Id is blank), so the reconcile sees a NEW worker and transiently runs BOTH the old and the
        /// edited one until the next idle rebuild. Removed / re-keyed workers are surfaced via a warning log
        /// for visibility (they are diffed but intentionally not applied — live removal is out of scope).
        /// </remarks>
        public int Reconcile(IReadOnlyList<ITranscriptionWorker> desired)
        {
            if (desired == null) throw new ArgumentNullException(nameof(desired));

            var addedPermits = 0;
            int workerCount;
            IReadOnlyList<string> added;
            IReadOnlyList<string> removed;
            lock (_gate)
            {
                // Diff BEFORE mutating _keys so added/removed describe the PRE-reconcile pool. Grow-only:
                // 'added' is applied by the loop below; 'removed' (a dropped or re-keyed worker) is only reported.
                (added, removed) = DiffWorkers(_keys, desired);

                var desiredKeys = ComputeKeys(desired);
                for (var i = 0; i < desired.Count; i++)
                {
                    var key = desiredKeys[i];
                    if (_byKey.ContainsKey(key)) continue;   // already live → leave its in-flight state alone
                    InitWorkerSlot(key, desired[i]);
                    var cap = Cap(desired[i]);
                    TotalCapacity += cap;
                    addedPermits += cap;
                }
                workerCount = _keys.Count;
            }

            // Release the added permits OUTSIDE _gate (never release inside a lock). The uncapped semaphore
            // makes Release(delta) legal; the map-add-BEFORE-release ordering means the woken waiter sees the
            // newly-added workers in PickLocked. Routed through ReleaseSlots so the over-release tripwire runs.
            if (addedPermits > 0) ReleaseSlots(addedPermits);

            // Surface the reconcile outcome (whisper-subs-9gq hardening): a hot-add is expected; a removal or
            // re-key is a known foot-gun (grow-only leaves it live until the next idle rebuild), so warn on it.
            if (added.Count > 0)
                _logger.LogInformation("WorkerPool hot-add: added workers [{Added}]", string.Join(",", added));
            if (removed.Count > 0)
                _logger.LogWarning(
                    "WorkerPool: worker(s) [{Removed}] were removed from config but stay live until the next idle pool rebuild / restart (live removal not yet supported)",
                    string.Join(",", removed));

            return workerCount;
        }

        // Central choke-point for every semaphore Release (whisper-subs-9gq hardening). The ctor now uses the
        // UNCAPPED SemaphoreSlim (so Reconcile can Release(delta) to grow capacity), which removed the
        // SemaphoreFullException that used to catch a double-release. This restores that tripwire as a LOG
        // (never a throw — a diagnostic must not crash dispatch): if the permit count ever exceeds the current
        // TotalCapacity ceiling, a Release was not paired 1:1 with an Acquire. TotalCapacity is read AFTER
        // Reconcile grows it under _gate, so the comparison always uses the current ceiling.
        private void ReleaseSlots(int n = 1)
        {
            _slots.Release(n);
            if (_slots.CurrentCount > TotalCapacity)
                _logger.LogError(
                    "WorkerPool permit over-release: CurrentCount {C} > TotalCapacity {T}",
                    _slots.CurrentCount, TotalCapacity);
        }

        // Caller holds _gate. Snapshots each worker as a WorkerSlot (keyed by the unique routing key), asks
        // WorkerScheduling for the cheapest capable free one, marks it in-flight, and returns a lease
        // carrying that key. Null if none is free-and-capable right now.
        private WorkerLease? PickLocked(JobRequirements job)
        {
            var slots = new List<WorkerSlot>(_keys.Count);
            foreach (var key in _keys)
                slots.Add(new WorkerSlot(key, true, _inFlight[key], _byKey[key].Capabilities));

            var pick = WorkerScheduling.Pick(slots, job);
            if (pick is null) return null;

            var chosenKey = pick.Value.Id;   // WorkerSlot.Id is the unique routing key set above
            _inFlight[chosenKey]++;
            return new WorkerLease(chosenKey, _byKey[chosenKey]);
        }
    }
}
