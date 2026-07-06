using System.Collections.Generic;
using System.Threading.Tasks;

namespace WhisperSubs.ScheduledTasks
{
    /// <summary>
    /// Pure helper for the scheduled sweep's bounded-producer dispatch (v4.1): trims the tracked
    /// in-flight task list so it stays bounded by pool capacity instead of growing with the library.
    /// Only tasks that ran to completion are dropped — a cancelled or faulted task MUST stay tracked
    /// so the sweep's final <see cref="Task.WhenAll(IEnumerable{Task})"/> still observes its
    /// exception (pruning on <see cref="Task.IsCompleted"/> would silently orphan it).
    /// </summary>
    internal static class InFlightTasks
    {
        /// <summary>
        /// Removes successfully completed tasks from <paramref name="inFlight"/>; returns how many
        /// were removed.
        /// </summary>
        internal static int PruneCompleted(List<Task> inFlight)
        {
            return inFlight.RemoveAll(t => t.IsCompletedSuccessfully);
        }
    }
}
