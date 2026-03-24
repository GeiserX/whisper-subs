using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using JellySubtitles.Providers;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace JellySubtitles.Controller
{
    public class SubtitleWorkItem
    {
        public required BaseItem Item { get; init; }
        public required string Language { get; init; }
        public TaskCompletionSource<bool>? Completion { get; init; }
    }

    /// <summary>
    /// Holds manually-requested (priority) subtitle jobs.
    /// These are drained first by the scheduled task between auto-generation items.
    /// </summary>
    public class SubtitleQueueService
    {
        private static SubtitleQueueService? _instance;
        public static SubtitleQueueService Instance => _instance ??= new SubtitleQueueService();

        private readonly ConcurrentQueue<SubtitleWorkItem> _priorityQueue = new();

        public Task EnqueuePriorityAsync(BaseItem item, string language)
        {
            var tcs = new TaskCompletionSource<bool>();
            _priorityQueue.Enqueue(new SubtitleWorkItem
            {
                Item = item,
                Language = language,
                Completion = tcs
            });
            return tcs.Task;
        }

        public bool TryDequeuePriority(out SubtitleWorkItem? item)
        {
            return _priorityQueue.TryDequeue(out item);
        }

        public int PriorityCount => _priorityQueue.Count;

        /// <summary>
        /// Process all pending priority items. Called between auto-generation items
        /// and from the manual Generate endpoint.
        /// </summary>
        public async Task DrainPriorityAsync(
            SubtitleManager manager,
            ISubtitleProvider provider,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            while (TryDequeuePriority(out var workItem) && workItem != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    logger.LogInformation("[Priority] Processing {ItemName}", workItem.Item.Name);
                    await manager.GenerateSubtitleAsync(
                        workItem.Item, provider, workItem.Language, cancellationToken);
                    workItem.Completion?.TrySetResult(true);
                }
                catch (OperationCanceledException)
                {
                    workItem.Completion?.TrySetCanceled();
                    throw;
                }
                catch (Exception ex)
                {
                    workItem.Completion?.TrySetException(ex);
                    logger.LogError(ex, "[Priority] Failed: {ItemName}", workItem.Item.Name);
                }
            }
        }
    }
}
