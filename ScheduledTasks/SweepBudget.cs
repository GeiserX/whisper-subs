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
            => maxRuntimeHours > 0 ? startUtc.AddHours(maxRuntimeHours) : (DateTime?)null;

        /// <summary>True once <paramref name="nowUtc"/> has reached a real deadline.</summary>
        internal static bool Expired(DateTime? deadline, DateTime nowUtc)
            => deadline.HasValue && nowUtc >= deadline.Value;
    }
}
