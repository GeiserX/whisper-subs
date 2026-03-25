using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using JellySubtitles.Providers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace JellySubtitles.Controller
{
    public class SubtitleWorkItem
    {
        public required BaseItem Item { get; init; }
        public required string Language { get; init; }
        public TaskCompletionSource<bool>? Completion { get; init; }
    }

    public class QueueEntry
    {
        public string ItemId { get; set; } = "";
        public string Language { get; set; } = "";
    }

    public class SubtitleQueueService
    {
        private static SubtitleQueueService? _instance;
        public static SubtitleQueueService Instance => _instance ??= new SubtitleQueueService();

        private readonly ConcurrentQueue<SubtitleWorkItem> _priorityQueue = new();
        private int _isDraining;
        private string? _currentItemName;
        private int _processedCount;
        private static readonly object _fileLock = new();

        public int PriorityCount => _priorityQueue.Count;
        public string? CurrentItemName => _currentItemName;
        public bool IsDraining => _isDraining == 1;
        public int ProcessedCount => _processedCount;

        private static string QueueFilePath
        {
            get
            {
                var pluginDir = Plugin.Instance?.DataFolderPath;
                if (string.IsNullOrEmpty(pluginDir)) return "";
                Directory.CreateDirectory(pluginDir);
                return Path.Combine(pluginDir, "queue.json");
            }
        }

        public void Enqueue(BaseItem item, string language)
        {
            _priorityQueue.Enqueue(new SubtitleWorkItem
            {
                Item = item,
                Language = language,
                Completion = null
            });
            PersistQueue();
        }

        public Task EnqueuePriorityAsync(BaseItem item, string language)
        {
            var tcs = new TaskCompletionSource<bool>();
            _priorityQueue.Enqueue(new SubtitleWorkItem
            {
                Item = item,
                Language = language,
                Completion = tcs
            });
            PersistQueue();
            return tcs.Task;
        }

        public bool TryDequeuePriority(out SubtitleWorkItem? item)
        {
            var result = _priorityQueue.TryDequeue(out item);
            if (result) PersistQueue();
            return result;
        }

        /// <summary>
        /// Restores queue from disk on startup. Call after Jellyfin library is available.
        /// </summary>
        public int RestoreQueue(ILibraryManager libraryManager, ILogger logger)
        {
            var path = QueueFilePath;
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return 0;

            try
            {
                var json = File.ReadAllText(path);
                var entries = JsonSerializer.Deserialize<List<QueueEntry>>(json);
                if (entries == null || entries.Count == 0) return 0;

                int restored = 0;
                foreach (var entry in entries)
                {
                    if (!Guid.TryParse(entry.ItemId, out var guid)) continue;
                    var item = libraryManager.GetItemById(guid);
                    if (item == null) continue;

                    _priorityQueue.Enqueue(new SubtitleWorkItem
                    {
                        Item = item,
                        Language = entry.Language,
                        Completion = null
                    });
                    restored++;
                }

                logger.LogInformation("[Queue] Restored {Count} items from disk (of {Total} saved)", restored, entries.Count);
                return restored;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Queue] Failed to restore queue from {Path}", path);
                return 0;
            }
        }

        private void PersistQueue()
        {
            var path = QueueFilePath;
            if (string.IsNullOrEmpty(path)) return;

            try
            {
                var entries = _priorityQueue.Select(item => new QueueEntry
                {
                    ItemId = item.Item.Id.ToString("N"),
                    Language = item.Language
                }).ToList();

                var json = JsonSerializer.Serialize(entries);
                lock (_fileLock)
                {
                    File.WriteAllText(path, json);
                }
            }
            catch
            {
                // Non-critical — best effort persistence
            }
        }

        /// <summary>
        /// Starts the background drain loop if not already running.
        /// Safe to call multiple times — only one drain runs at a time.
        /// Re-checks queue after draining to avoid race with late enqueues.
        /// </summary>
        public void EnsureDraining(
            SubtitleManager manager,
            ISubtitleProvider provider,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            if (Interlocked.CompareExchange(ref _isDraining, 1, 0) == 0)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await DrainLoopAsync(manager, provider, logger, cancellationToken);
                    }
                    finally
                    {
                        _currentItemName = null;
                        Interlocked.Exchange(ref _isDraining, 0);

                        // Re-check: if items were enqueued during the finally block,
                        // restart the drain to avoid stuck items.
                        if (!_priorityQueue.IsEmpty)
                        {
                            EnsureDraining(manager, provider, logger, cancellationToken);
                        }
                    }
                }, cancellationToken);
            }
        }

        private async Task DrainLoopAsync(
            SubtitleManager manager,
            ISubtitleProvider provider,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            while (TryDequeuePriority(out var workItem) && workItem != null)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // SubtitleManager handles skip/resume logic (checks completeness via duration comparison)
                try
                {
                    _currentItemName = workItem.Item.Name;
                    logger.LogInformation("[Queue] Processing {ItemName} ({Remaining} remaining)",
                        workItem.Item.Name, _priorityQueue.Count);
                    await manager.GenerateSubtitleAsync(
                        workItem.Item, provider, workItem.Language, cancellationToken);
                    Interlocked.Increment(ref _processedCount);
                    workItem.Completion?.TrySetResult(true);
                }
                catch (OperationCanceledException)
                {
                    workItem.Completion?.TrySetCanceled();
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _processedCount);
                    workItem.Completion?.TrySetException(ex);
                    logger.LogError(ex, "[Queue] Failed: {ItemName}", workItem.Item.Name);
                }
            }
            logger.LogInformation("[Queue] Drain complete. Processed {Count} items total.", _processedCount);
        }

        /// <summary>
        /// Process all pending priority items. Called by scheduled task.
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
                    _currentItemName = workItem.Item.Name;
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
            _currentItemName = null;
        }
    }
}
