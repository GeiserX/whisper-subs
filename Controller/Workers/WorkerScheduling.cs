using System;
using System.Collections.Generic;

namespace WhisperSubs.Controller.Workers
{
    /// <summary>
    /// Pure worker-selection policy (v4.0), mirroring the codebase's other unit-tested decision helpers
    /// (<see cref="PriorityScheduling"/>). Given the highest-priority job already chosen by the queue, it
    /// picks the cheapest capable worker with a free slot. "Prefer local, burst to a paid worker only when
    /// every local one is saturated" falls out of the cost-weighted score — no special-casing, no backend
    /// names in the logic.
    /// </summary>
    public static class WorkerScheduling
    {
        /// <summary>Hard constraints — can this worker serve this job right now?</summary>
        public static bool CanServe(WorkerSlot s, JobRequirements job)
            => s.Healthy
               && s.InFlight < s.Capabilities.MaxConcurrency
               && (!job.Translate || s.Capabilities.CanTranslate)
               && (job.RequiredModel is null
                   || s.Capabilities.Models.Count == 0            // empty = serves any model
                   || s.Capabilities.Models.Contains(job.RequiredModel));

        /// <summary>
        /// Soft preference — lower is better. A local worker's <c>CostWeight</c> is 0, so its score is
        /// dominated by load and it always beats any paid worker; a paid worker (CostWeight &gt; 0) is only
        /// ever the minimum when no local worker can serve the job.
        /// </summary>
        public static double Score(WorkerSlot s)
            => s.Capabilities.CostWeight * 1_000_000.0   // 0 for local ⇒ strictly beats any paid worker
               + s.InFlight * 1000.0                     // spread load: prefer the least-busy
               + s.Capabilities.Priority;                // stable, admin-tunable tiebreak

        /// <summary>
        /// The best worker for a job: the minimum-<see cref="Score"/> worker that <see cref="CanServe"/>s it,
        /// ties broken by ordinal Id for determinism. Null means nothing is feasible right now — the caller
        /// re-queues the job (never fails it).
        /// </summary>
        public static WorkerSlot? Pick(IReadOnlyList<WorkerSlot> slots, JobRequirements job)
        {
            WorkerSlot? best = null;
            double bestScore = double.MaxValue;
            foreach (var s in slots)
            {
                if (!CanServe(s, job)) continue;
                var score = Score(s);
                bool better = best is null
                    || score < bestScore
                    || (score == bestScore && string.CompareOrdinal(s.Id, best.Value.Id) < 0);
                if (better)
                {
                    best = s;
                    bestScore = score;
                }
            }
            return best;
        }
    }
}
