using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using WhisperSubs.Configuration;
using WhisperSubs.Providers;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace WhisperSubs.Controller
{
    /// <summary>
    /// Turns an approved (or auto-approved) user request into queued work (#112): re-resolves the item,
    /// expands a container to its leaves, enqueues each NON-FORCED at the request's tier, and starts the
    /// drain worker. Shared by the auto-approve path (request controller) and the admin-approve path so
    /// both enqueue identically. Non-forced means a user request never regenerates over a good subtitle.
    /// </summary>
    [ExcludeFromCodeCoverage(Justification = "Orchestrates Jellyfin runtime services (library manager, queue, provider)")]
    public static class SubtitleRequestService
    {
        /// <summary>
        /// Expands <paramref name="itemId"/> to leaf media and enqueues each at <paramref name="tier"/>.
        /// Returns the number of newly-queued items. A vanished item or empty container is a safe no-op.
        /// </summary>
        public static int EnqueueRequest(
            string itemId,
            string language,
            PriorityTier tier,
            PluginConfiguration config,
            ILibraryManager libraryManager,
            ILoggerFactory loggerFactory,
            ILogger logger)
        {
            if (!Guid.TryParse(itemId, out var guid)) return 0;

            var item = libraryManager.GetItemById(guid);
            if (item == null) return 0;

            var leaves = MediaItemResolver.ResolveLeafItems(item, config.EnableLyricsGeneration, libraryManager);
            if (leaves.Count == 0) return 0;

            var queue = SubtitleQueueService.Instance;
            int queued = 0;
            foreach (var leaf in leaves)
            {
                if (queue.Enqueue(leaf, language, tier)) queued++;
            }

            var manager = new SubtitleManager(libraryManager, loggerFactory.CreateLogger<SubtitleManager>());
            var provider = SubtitleProviderFactory.Create(config, loggerFactory);
            queue.EnsureDraining(manager, provider, logger, CancellationToken.None);

            logger.LogInformation("[Request] Enqueued {Queued} of {Total} item(s) for {ItemId} [{Tier}]",
                queued, leaves.Count, itemId, tier);
            return queued;
        }
    }
}
