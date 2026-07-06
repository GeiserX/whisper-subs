using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

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
        private readonly SemaphoreSlim _slots;

        public WorkerPool(IReadOnlyList<ITranscriptionWorker> workers)
        {
            if (workers == null) throw new ArgumentNullException(nameof(workers));

            var capacity = 0;
            for (var i = 0; i < workers.Count; i++)
            {
                var w = workers[i];
                // Guarantee a unique routing key even if two configured workers collide on Id — a
                // duplicate would otherwise make the reverse Id→worker lookup ambiguous. The key is only
                // used internally for slot accounting; WorkerSlot.Id still carries it for the deterministic
                // ordinal tiebreak in WorkerScheduling.Pick.
                var key = _byKey.ContainsKey(w.Id) ? $"{w.Id}#{i}" : w.Id;
                _keys.Add(key);
                _byKey[key] = w;
                _inFlight[key] = 0;
                capacity += w.Capabilities.MaxConcurrency < 1 ? 1 : w.Capabilities.MaxConcurrency;
            }

            TotalCapacity = capacity < 1 ? 1 : capacity;
            _slots = new SemaphoreSlim(TotalCapacity, TotalCapacity);
        }

        /// <summary>Summed MaxConcurrency across all workers (≥1): how many jobs may run at once.</summary>
        public int TotalCapacity { get; }

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
                        w.Id, w.Name, Healthy: true, _inFlight[key],
                        caps.MaxConcurrency < 1 ? 1 : caps.MaxConcurrency, caps.IsLocal, caps.CostWeight));
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
                _slots.Release();
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>Releases a slot after a job completes or fails, by the <see cref="WorkerLease.Key"/>. Pair each <see cref="AcquireAsync"/> with exactly one Release.</summary>
        public void Release(string leaseKey)
        {
            lock (_gate)
            {
                if (_inFlight.TryGetValue(leaseKey, out var n) && n > 0)
                    _inFlight[leaseKey] = n - 1;
            }
            _slots.Release();
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
