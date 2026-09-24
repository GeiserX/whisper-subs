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

        /// <summary>
        /// A second lease, on a worker that takes whole titles, when the job's own worker does not
        /// (<see cref="NeedsProbeWorker"/>). The job runs the whisper language probe under it and releases it
        /// before its Canary engine starts. Null otherwise.
        /// </summary>
        public WorkerLease? ProbeLease { get; private set; }

        /// <summary>
        /// The job needs a probe worker but no worker in the pool runs Whisper at all, so it was placed
        /// without one: tagged audio needs no probe, and untagged audio must fail with "language unknown"
        /// rather than be guessed by Canary, which cannot identify languages.
        /// </summary>
        public bool NoProbeWorkerInPool { get; private set; }

        /// <summary>
        /// Whether a job placed on <paramref name="worker"/> also needs a whole-title worker for the language
        /// probe: a translate job (target-only requirement) on a worker that runs no Whisper. Pure.
        /// </summary>
        public static bool NeedsProbeWorker(JobRequirements job, WorkerCapabilities worker)
            => job.TargetOnly && !worker.TranscribesItems;

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
            if (lease is { } placed)
            {
                if (!NeedsProbeWorker(job, placed.Worker.Capabilities))
                {
                    Lease = placed;
                    return LaneVisit.Take;
                }

                if (!_pool.HasCapableWorker(WorkerJob.LanguageProbe))
                {
                    Lease = placed;
                    NoProbeWorkerInPool = true;
                    return LaneVisit.Take;
                }

                // The probe must run under a real lease, never on a worker that may be busy or out of
                // rotation: that would put two Whisper processes on one GPU, or fail on a busy remote and
                // leave the job with an unknown language. No whole-title worker free: leave the job queued.
                var probe = _pool.TryAcquire(WorkerJob.LanguageProbe);
                if (probe != null)
                {
                    Lease = placed;
                    ProbeLease = probe;
                    return LaneVisit.Take;
                }
                _pool.GiveBack(placed);
                job = WorkerJob.LanguageProbe;   // what the job waits for
            }

            if (_pool.HasAvailableWorker(job)) _waitsForBusyWorker = true;
            else UnavailableRequirement ??= job;
            return LaneVisit.Skip;
        }
    }

    /// <summary>
    /// The language-probe provider for a translate job when no worker in the pool runs Whisper. It never
    /// guesses: the check fails with the reason, the pass records the language as unknown, and an untagged
    /// title is refused instead of being sent to Canary as English.
    /// </summary>
    internal sealed class NoLanguageProbeProvider : Providers.ISubtitleProvider
    {
        public static readonly NoLanguageProbeProvider Instance = new();

        public string Name => "No language probe";

        public bool RequiresSpeechAlignmentOptIn => false;

        public System.Threading.Tasks.Task<string> TranscribeAsync(string audioPath, string language, System.Threading.CancellationToken ct, bool translate = false, string? targetLanguage = null)
            => System.Threading.Tasks.Task.FromException<string>(new NotSupportedException("This provider only answers the language check."));

        public System.Threading.Tasks.Task<(string Language, float Probability)> DetectLanguageAsync(string audioPath, System.Threading.CancellationToken ct)
            => System.Threading.Tasks.Task.FromException<(string, float)>(
                new InvalidOperationException("No worker in the pool runs Whisper, so the language of audio without language tags cannot be checked."));
    }
}
