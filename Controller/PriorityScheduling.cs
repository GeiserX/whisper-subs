using System;
using System.Collections.Generic;

namespace WhisperSubs.Controller
{
    /// <summary>
    /// Pure, unit-tested decision helpers for the priority queue and user-request feature: how a
    /// requester maps to a tier, how a legacy persisted entry is normalised, and the per-user request
    /// quota window. No I/O, no Jellyfin types. (Issue #112.)
    /// </summary>
    public static class PriorityScheduling
    {
        /// <summary>Lowest tier number = highest priority.</summary>
        internal const int MinTier = (int)PriorityTier.Critical; // 0
        /// <summary>Highest tier number = lowest priority.</summary>
        internal const int MaxTier = (int)PriorityTier.Background; // 4

        /// <summary>Clamps an arbitrary integer to a valid <see cref="PriorityTier"/>.</summary>
        public static PriorityTier ClampTier(int tier)
            => (PriorityTier)Math.Clamp(tier, MinTier, MaxTier);

        /// <summary>
        /// Server-side tier assignment. The tier comes solely from the server-known
        /// <paramref name="requester"/> and the admin's configured mapping — never from client input.
        /// </summary>
        public static PriorityTier ResolveTier(
            RequesterKind requester,
            PriorityTier adminTier,
            PriorityTier userTier,
            PriorityTier backgroundTier)
            => requester switch
            {
                RequesterKind.Admin => adminTier,
                RequesterKind.User => userTier,
                RequesterKind.BackgroundSweep => backgroundTier,
                _ => PriorityTier.Medium
            };

        /// <summary>
        /// Normalises a tier restored from queue.json. Legacy entries written before this feature carry
        /// no tier (<paramref name="persistedTier"/> is null); they were all admin manual/bulk work, so
        /// they map to <see cref="PriorityTier.High"/> — deliberately NOT Critical(0), which a naive
        /// "missing int defaults to 0" would wrongly pick and let old entries outrank every new admin
        /// request. A present value is clamped to the valid range.
        /// </summary>
        public static PriorityTier NormalizeRestoredTier(int? persistedTier)
            => persistedTier is int t ? ClampTier(t) : PriorityTier.High;

        /// <summary>
        /// The stronger (higher-priority, lower-numbered) of two tiers. Used when the same item is
        /// requested twice so the job keeps the best priority anyone asked for.
        /// </summary>
        public static PriorityTier Stronger(PriorityTier a, PriorityTier b)
            => (int)a <= (int)b ? a : b;

        /// <summary>
        /// Pure sliding-window quota check. Given the ticks at which a user's prior requests were
        /// created, prunes those outside the window ending at <paramref name="nowTicks"/> and reports
        /// whether one more request fits under <paramref name="limit"/>. <paramref name="limit"/> &lt;= 0
        /// means unlimited; <paramref name="windowTicks"/> &lt;= 0 means "do not prune by time" (the
        /// limit then applies to all supplied timestamps). Returns the kept-and-sorted timestamps
        /// (what to persist) alongside the decision.
        /// </summary>
        public static QuotaDecision EvaluateQuota(
            IEnumerable<long> priorRequestTicks,
            long nowTicks,
            long windowTicks,
            int limit)
        {
            var kept = new List<long>();
            foreach (var t in priorRequestTicks)
            {
                if (windowTicks <= 0 || nowTicks - t < windowTicks)
                {
                    kept.Add(t);
                }
            }
            kept.Sort();

            bool allowed = limit <= 0 || kept.Count < limit;
            return new QuotaDecision(allowed, kept.Count, limit, kept);
        }
    }

    /// <summary>Result of a <see cref="PriorityScheduling.EvaluateQuota"/> check.</summary>
    public sealed class QuotaDecision
    {
        public QuotaDecision(bool allowed, int usedInWindow, int limit, IReadOnlyList<long> keptTicks)
        {
            Allowed = allowed;
            UsedInWindow = usedInWindow;
            Limit = limit;
            KeptTicks = keptTicks;
        }

        /// <summary>Whether one more request is permitted.</summary>
        public bool Allowed { get; }

        /// <summary>How many prior requests fall inside the window.</summary>
        public int UsedInWindow { get; }

        /// <summary>The configured limit (0 or less = unlimited).</summary>
        public int Limit { get; }

        /// <summary>Prior timestamps still inside the window, sorted ascending — persist these.</summary>
        public IReadOnlyList<long> KeptTicks { get; }
    }
}
