using System;

namespace WhisperSubs.ScheduledTasks
{
    /// <summary>
    /// Wall-clock budget for one sweep of the subtitle generation task.
    /// Kept separate from the task so the boundary is unit-testable without a Jellyfin host.
    /// </summary>
    internal static class SweepBudget
    {
        /// <summary>
        /// Deadline for a sweep started at <paramref name="startUtc"/>, or null when the cap is
        /// disabled (<paramref name="maxRuntimeHours"/> of 0 or less means unlimited).
        /// </summary>
        internal static DateTime? Deadline(int maxRuntimeHours, DateTime startUtc)
        {
            if (maxRuntimeHours <= 0) return null;

            // A misconfigured huge value must not throw out of AddHours and kill the task; an
            // unreachable deadline behaves like "effectively unlimited", which is what was asked for.
            var remainingHours = (DateTime.MaxValue - startUtc).TotalHours;
            return maxRuntimeHours >= remainingHours ? DateTime.MaxValue : startUtc.AddHours(maxRuntimeHours);
        }

        /// <summary>True once <paramref name="nowUtc"/> has reached a real deadline.</summary>
        internal static bool Expired(DateTime? deadline, DateTime nowUtc)
            => deadline.HasValue && nowUtc >= deadline.Value;
    }
}
