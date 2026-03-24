using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JellySubtitles.Controller;
using JellySubtitles.Providers;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace JellySubtitles.ScheduledTasks
{
    public class SubtitleGenerationTask : IScheduledTask
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger<SubtitleGenerationTask> _logger;
        private readonly ILogger<SubtitleManager> _managerLogger;
        private readonly ILogger<WhisperProvider> _providerLogger;

        public SubtitleGenerationTask(
            ILibraryManager libraryManager,
            ILogger<SubtitleGenerationTask> logger,
            ILogger<SubtitleManager> managerLogger,
            ILogger<WhisperProvider> providerLogger)
        {
            _libraryManager = libraryManager;
            _logger = logger;
            _managerLogger = managerLogger;
            _providerLogger = providerLogger;
        }

        public string Name => "Generate Subtitles";
        public string Key => "JellySubtitlesGenerator";
        public string Description => "Scans enabled libraries and generates subtitles for items that lack them.";
        public string Category => "JellySubtitles";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return Array.Empty<TaskTriggerInfo>();
        }

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Starting subtitle generation task");

            var config = Plugin.Instance.Configuration;
            if (!config.EnableAutoGeneration)
            {
                _logger.LogInformation("Auto-generation is disabled in configuration");
                return;
            }

            if (string.IsNullOrEmpty(config.WhisperModelPath))
            {
                _logger.LogWarning("Whisper model path is not configured, aborting task");
                return;
            }

            var manager = new SubtitleManager(_libraryManager, _managerLogger);
            var provider = new WhisperProvider(_providerLogger, config.WhisperModelPath, config.WhisperBinaryPath);
            var language = config.DefaultLanguage;

            // Collect all video items from enabled libraries
            var enabledLibraryIds = config.EnabledLibraries
                .Where(id => !string.IsNullOrEmpty(id))
                .Select(id => Guid.Parse(id))
                .ToList();

            if (enabledLibraryIds.Count == 0)
            {
                // If no libraries are explicitly enabled, scan all libraries
                var allLibraries = _libraryManager.GetVirtualFolders();
                enabledLibraryIds = allLibraries
                    .Select(vf => Guid.Parse(vf.ItemId))
                    .ToList();
                _logger.LogInformation("No libraries explicitly enabled, scanning all {Count} libraries", enabledLibraryIds.Count);
            }

            var allItems = new List<Video>();

            foreach (var libraryId in enabledLibraryIds)
            {
                var items = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    ParentId = libraryId,
                    IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
                    Recursive = true
                }).OfType<Video>()
                 .Where(v => !v.HasSubtitles)
                 .ToList();

                allItems.AddRange(items);
            }

            _logger.LogInformation("Found {Count} items without subtitles across {LibCount} libraries",
                allItems.Count, enabledLibraryIds.Count);

            if (allItems.Count == 0)
            {
                progress.Report(100);
                return;
            }

            var completed = 0;
            var failed = 0;

            foreach (var item in allItems)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    _logger.LogInformation("Processing [{Current}/{Total}] {ItemName}",
                        completed + 1, allItems.Count, item.Name);

                    await manager.GenerateSubtitleAsync(item, provider, language, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogError(ex, "Failed to generate subtitle for {ItemName}", item.Name);
                }

                completed++;
                progress.Report((double)completed / allItems.Count * 100);
            }

            _logger.LogInformation("Subtitle generation task complete. Processed: {Processed}, Failed: {Failed}",
                completed, failed);
        }
    }
}
