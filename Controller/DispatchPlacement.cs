using System;
using System.Collections.Generic;
using WhisperSubs.Controller.Workers;

namespace WhisperSubs.Controller
{
    /// <summary>
    /// How the dispatcher chooses the next job and its worker together, so it never takes a job off the
    /// queue that it cannot place. It walks the queue in priority order and takes the first job a free
    /// worker can serve right now, leased on that worker. A translate job therefore always starts on a
    /// worker that lists its target: it never holds a slot it cannot use, and never gives one back to wait
    /// for another worker while the dispatcher keeps dequeuing behind it.
    /// </summary>
    internal static class DispatchPlacement
    {
        /// <summary>
        /// What a queued job needs from its worker. A generate job needs the drain's item requirements, or
        /// can never run (null) when the pool takes no whole item. A translate job needs a worker that lists
        /// its target. When no worker lists it at all, it takes any worker, so the pass fails it with the
        /// target's own final reason instead of leaving it queued forever. Pure.
        /// </summary>
        public static JobRequirements? RequirementsOf(
            string? target, JobRequirements itemRequirements, bool poolServesItems, bool localWhisperTranslates,
            Func<JobRequirements, bool> anyWorkerCanServe)
        {
            if (target == null) return poolServesItems ? itemRequirements : null;
            var job = WorkerJob.ForTarget(target, localWhisperTranslates);
            return anyWorkerCanServe(job) ? job : WorkerJob.AnyWorker;
        }
    }

    /// <summary>
    /// One walk over the queue (<see cref="PriorityLanes{T}.TryTakeFirst"/>): decides, job by job, whether
    /// to take it, and leases its worker when it does. Each distinct requirement is tried once per walk, so
    /// a long queue of jobs that all wait for one busy worker costs one lease attempt, not one per job.
    /// </summary>
    internal sealed class DispatchScan
    {
        private readonly WorkerPool _pool;
        private readonly HashSet<JobRequirements> _tried = new();
        private bool _waitsForBusyWorker;

        public DispatchScan(WorkerPool pool) => _pool = pool;

        /// <summary>The lease for the job the walk took; null when it took a job no worker can ever serve.</summary>
        public WorkerLease? Lease { get; private set; }

        /// <summary>A requirement whose every capable worker is out of rotation, for the pause reason.</summary>
        public JobRequirements? UnavailableRequirement { get; private set; }

        /// <summary>
        /// True when the walk placed nothing and every job it looked at waits only for workers out of
        /// rotation. Waiting cannot help then (only a fresh probe between drains puts them back), so the
        /// drain pauses. False when some job waits for a busy worker: the dispatcher waits for a release.
        /// </summary>
        public bool BlockedOnlyByUnavailable => UnavailableRequirement != null && !_waitsForBusyWorker;

        public LaneVisit Visit(JobRequirements? requirements)
        {
            if (requirements is not { } job)
            {
                Lease = null;   // never servable: take it so the dispatcher fails it now
                return LaneVisit.Take;
            }

            if (_pool.FreeSlots == 0)
            {
                _waitsForBusyWorker = true;
                return LaneVisit.Stop;
            }

            if (!_tried.Add(job)) return LaneVisit.Skip;

            var lease = _pool.TryAcquire(job);
            if (lease != null)
            {
                Lease = lease;
                return LaneVisit.Take;
            }

            if (_pool.HasAvailableWorker(job)) _waitsForBusyWorker = true;
            else UnavailableRequirement ??= job;
            return LaneVisit.Skip;
        }
    }
}
