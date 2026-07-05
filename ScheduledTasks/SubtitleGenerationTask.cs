using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WhisperSubs.Controller;
using WhisperSubs.Providers;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace WhisperSubs.ScheduledTasks
{
    // Orchestration over Jellyfin runtime services (library manager, session manager, live filesystem)
    // + the whisper pipeline; the unit-testable decision logic lives in pure helpers
    // (SubtitleManager.IsSubtitleSetComplete, SubtitleSkipCache, SubtitleInventory). Excluded from
    // coverage as a whole — coverlet.runsettings excludes this type locally, but CI's --collect ignores
    // runsettings, so the attribute is what makes the exclusion effective in both places.
    [ExcludeFromCodeCoverage(Justification = "Scheduled-task orchestration over Jellyfin runtime; logic lives in unit-tested pure helpers")]
    public class SubtitleGenerationTask : IScheduledTask
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ISessionManager _sessionManager;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<SubtitleGenerationTask> _logger;

        public SubtitleGenerationTask(
            ILibraryManager libraryManager,
            ISessionManager sessionManager,
            ILogger<SubtitleGenerationTask> logger,
            ILoggerFactory loggerFactory)
        {
            _libraryManager = libraryManager;
            _sessionManager = sessionManager;
            _logger = logger;
            _loggerFactory = loggerFactory;
        }

        public string Name => "Generate Subtitles";
        public string Key => "WhisperSubsGenerator";
        public string Description => "Scans enabled libraries and generates subtitles for items that lack them. Resumes automatically after restart.";
        public string Category => "WhisperSubs";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.DailyTrigger,
                    TimeOfDayTicks = TimeSpan.FromHours(2).Ticks
                },
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.StartupTrigger
                }
            };
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

            if (string.IsNullOrWhiteSpace(config.RemoteWhisperApiUrl) && string.IsNullOrWhiteSpace(config.WhisperModelPath))
            {
                _logger.LogWarning("Neither remote API URL nor local model path is configured, aborting task");
                return;
            }

            var manager = new SubtitleManager(_libraryManager, _loggerFactory.CreateLogger<SubtitleManager>());
            var provider = SubtitleProviderFactory.Create(config, _loggerFactory);
            var language = config.DefaultLanguage;
            var queue = SubtitleQueueService.Instance;

            // Restore persisted queue from disk (survives restarts)
            var restored = queue.RestoreQueue(_libraryManager, _logger);
            if (restored > 0)
            {
                _logger.LogInformation("Draining {Count} restored priority items before auto-generation", restored);
                await queue.DrainPriorityAsync(manager, provider, _logger, cancellationToken);
            }

            // Collect items — the query is fast (DB lookup), no bulk in-memory storage needed
            var enabledLibraryIds = config.EnabledLibraries
                .Where(id => !string.IsNullOrEmpty(id))
                .Select(id => Guid.Parse(id))
                .ToList();

            if (enabledLibraryIds.Count == 0)
            {
                var allLibraries = _libraryManager.GetVirtualFolders();
                enabledLibraryIds = allLibraries
                    .Select(vf => Guid.Parse(vf.ItemId))
                    .ToList();
                _logger.LogInformation("No libraries explicitly enabled, scanning all {Count} libraries", enabledLibraryIds.Count);
            }

            // In ForcedOnly/FullAndForced modes, items with full subtitles but no forced
            // subtitles must still be considered. Only filter by HasSubtitles in Full mode.
            var needsForced = config.SubtitleMode == Configuration.SubtitleMode.ForcedOnly
                || config.SubtitleMode == Configuration.SubtitleMode.FullAndForced;
            var needsTranslation = config.SubtitleMode == Configuration.SubtitleMode.TranslationOnly
                || (config.EnableTranslation
                    && (config.SubtitleMode == Configuration.SubtitleMode.Full
                        || config.SubtitleMode == Configuration.SubtitleMode.FullAndForced));

            var includeKinds = new List<BaseItemKind> { BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video };
            if (config.EnableLyricsGeneration)
            {
                includeKinds.Add(BaseItemKind.Audio);
            }

            var allItems = new List<(BaseItem Item, string LibraryName)>();
            foreach (var libraryId in enabledLibraryIds)
            {
                var library = _libraryManager.GetItemById(libraryId);
                var libraryName = library?.Name ?? "Unknown";

                var items = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    ParentId = libraryId,
                    IncludeItemTypes = includeKinds.ToArray(),
                    Recursive = true
                });

                foreach (var queryItem in items)
                {
                    // Skip virtual/placeholder items with no media file
                    if (string.IsNullOrEmpty(queryItem.Path)) continue;

                    if (queryItem is Video video)
                    {
                        if (!needsForced && !needsTranslation && video.HasSubtitles) continue;
                        allItems.Add((video, libraryName));
                    }
                    else if (queryItem is MediaBrowser.Controller.Entities.Audio.Audio)
                    {
                        allItems.Add((queryItem, libraryName));
                    }
                }
            }

            _logger.LogInformation("Found {Count} candidate items across {LibCount} libraries",
                allItems.Count, enabledLibraryIds.Count);

            if (allItems.Count == 0)
            {
                progress.Report(100);
                return;
            }

            var completed = 0;
            var failed = 0;
            queue.ReportTaskProgress(null, 0, allItems.Count, 0);

            // Issue #110: the skip-cache lets repeat runs skip the per-item filesystem probe for
            // unchanged, already-satisfied items. Keyed on the item change token (DateLastSaved) + a
            // settings signature; persisted in the finally below so an interrupted run keeps the
            // progress it made (each entry is independently valid — no global high-water mark).
            var cachePath = SubtitleSkipCache.DefaultPath();
            var cacheSignature = SubtitleSkipCache.ComputeSignature(config);
            var skipCache = (config.CacheSkippedItems && !string.IsNullOrEmpty(cachePath))
                ? SubtitleSkipCache.Load(cachePath, cacheSignature, _logger)
                : null;
            var nowTicks = DateTime.UtcNow.Ticks;
            var candidateIds = new HashSet<Guid>(allItems.Select(a => a.Item.Id));
            if (skipCache != null)
            {
                _logger.LogInformation("Skip cache active: {Count} remembered item(s)", skipCache.Count);
            }

            try
            {
            for (int i = 0; i < allItems.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Wait for active playback to finish before processing next item
                if (config.PauseOnPlayback)
                {
                    await WaitForPlaybackIdleAsync(cancellationToken);
                }

                // Drain any priority (manual) requests first
                if (queue.PriorityCount > 0)
                {
                    _logger.LogInformation("Pausing auto-generation to process {Count} priority request(s)", queue.PriorityCount);
                    await queue.DrainPriorityAsync(manager, provider, _logger, cancellationToken);
                }

                var (item, libName) = allItems[i];
                var itemType = item.GetType().Name;

                // Issue #110 fast-path: if a previous run recorded this (unchanged) video as already
                // satisfied under the current settings, skip the filesystem/stream probe entirely.
                if (skipCache != null && item is Video cacheVideo)
                {
                    var token = cacheVideo.DateLastSaved.Ticks;
                    if (SubtitleSkipCache.CanSkip(skipCache.TryGet(item.Id), token, nowTicks, config.SkipCacheExpiryDays))
                    {
                        _logger.LogInformation("[{Current}/{Total}] Skipping {ItemName}: already satisfied (cached)",
                            completed + 1, allItems.Count, item.Name);
                        completed++;
                        queue.ReportTaskProgress(null, completed, allItems.Count, failed);
                        progress.Report((double)completed / allItems.Count * 100);
                        continue;
                    }
                }

                // For Audio items (lyrics), skip if .lrc already exists
                if (item is MediaBrowser.Controller.Entities.Audio.Audio)
                {
                    try
                    {
                        var audioPath = item.Path;
                        if (!string.IsNullOrEmpty(audioPath))
                        {
                            var audioDir = System.IO.Path.GetDirectoryName(audioPath);
                            var audioBase = System.IO.Path.GetFileNameWithoutExtension(audioPath);
                            if (audioDir != null)
                            {
                                // Check Jellyfin-standard track.lrc and language-tagged track.*.lrc
                                var exactLrc = System.IO.Path.Combine(audioDir, audioBase + ".lrc");
                                if (System.IO.File.Exists(exactLrc) || System.IO.Directory.GetFiles(audioDir, audioBase + ".*.lrc").Length > 0)
                                {
                                    completed++;
                                    queue.ReportTaskProgress(null, completed, allItems.Count, failed);
                                    progress.Report((double)completed / allItems.Count * 100);
                                    continue;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error checking lyrics for {ItemName}, will attempt generation", item.Name);
                    }
                }

                // Skip if subtitle was already generated (e.g. from a previous run before restart)
                var mediaPath = item.Path;
                if (!string.IsNullOrEmpty(mediaPath))
                {
                    var baseName = System.IO.Path.GetFileNameWithoutExtension(mediaPath);
                    var dir = System.IO.Path.GetDirectoryName(mediaPath);
                    if (dir != null)
                    {
                        // Issue #101: subtitles may live in the media folder OR the item's internal
                        // metadata path (read-only / save-with-media-off libraries), so look in both.
                        var existingFiles = SubtitleManager.FindGeneratedFiles(item, dir, baseName + ".*.generated.srt").ToArray();
                        var noForeignMarkers = SubtitleManager.FindGeneratedFiles(item, dir, baseName + ".*.forced.noforeignlang").ToArray();
                        var hasFullSrt = existingFiles.Any(f => !System.IO.Path.GetFileName(f).Contains(".forced."));

                        // Also check for user-provided external subtitle files (non-forced, non-generated).
                        // Issue #83: image sidecars (.sub/.sup) only count when CountImageSubtitlesAsPresent
                        // is on — otherwise a text subtitle should still be generated. Shared helper keeps
                        // this in lockstep with the translation "auto" fallback and the stream predicate.
                        if (!hasFullSrt)
                        {
                            var subtitleExts = SubtitleInventory.UsableSubtitleExtensions(!config.CountImageSubtitlesAsPresent);
                            hasFullSrt = System.IO.Directory.GetFiles(dir, baseName + ".*")
                                .Any(f =>
                                {
                                    var name = System.IO.Path.GetFileName(f);
                                    var ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                                    return subtitleExts.Contains(ext)
                                        && !name.Contains(".forced.")
                                        && !name.Contains(".generated.");
                                });
                        }

                        // Check for embedded subtitle streams (MKV, MP4, etc.)
                        if (!hasFullSrt && item is Video embeddedCheck && embeddedCheck.HasSubtitles)
                        {
                            // Issue #82: HasSubtitles is language- and type-blind, so a forced-only
                            // or image-only embedded track would wrongly satisfy the full pass. When
                            // SkipIfSubtitleExists is on, prefer a stream-aware check that requires a
                            // text track (and a non-forced one when IgnoreForcedSubtitles is on).
                            // Bias toward generating — if no usable track is found, leave it false.
                            if (config.SkipIfSubtitleExists)
                            {
                                hasFullSrt = HasUsableSubtitleStream(item,
                                    ignoreForced: config.IgnoreForcedSubtitles,
                                    requireText: !config.CountImageSubtitlesAsPresent);
                            }
                            else
                            {
                                hasFullSrt = true;
                            }
                        }
                        var hasForcedSrt = existingFiles.Any(f => System.IO.Path.GetFileName(f).Contains(".forced.")) || noForeignMarkers.Length > 0;

                        var hasTranslatedSrt = false;
                        if (needsTranslation && dir != null)
                        {
                            hasTranslatedSrt = SubtitleManager.GeneratedFileExists(
                                item, System.IO.Path.Combine(dir, baseName + ".en.translated.srt"));

                            // Issue #82: an existing usable English subtitle stream (embedded OR
                            // external) satisfies the translation need just as a .en.translated.srt
                            // would — so a foreign-audio movie that already ships English subs is not
                            // needlessly re-translated (~7h saved per item).
                            if (!hasTranslatedSrt && config.SkipIfSubtitleExists)
                            {
                                hasTranslatedSrt = SubtitleInventory.HasUsableSubtitle(
                                    SubtitleStreamReader.GetSubtitleStreams(item), "en",
                                    ignoreForced: config.IgnoreForcedSubtitles,
                                    requireText: !config.CountImageSubtitlesAsPresent);
                            }
                        }

                        bool alreadyComplete = SubtitleManager.IsSubtitleSetComplete(
                            config.SubtitleMode, needsTranslation, hasFullSrt, hasForcedSrt, hasTranslatedSrt);

                        // Issue #110: remember the verdict for unchanged future runs. Record only a
                        // positive (complete) result; a not-complete video is removed so it is always
                        // re-evaluated until generated (bias toward generating). Videos only — audio
                        // lyrics keep their own cheap .lrc fast-path above.
                        if (skipCache != null && item is Video)
                        {
                            if (alreadyComplete)
                            {
                                skipCache.Record(item.Id, new SubtitleSkipCache.Entry
                                {
                                    Token = item.DateLastSaved.Ticks,
                                    Full = hasFullSrt,
                                    Forced = hasForcedSrt,
                                    Translated = hasTranslatedSrt,
                                    CachedAtTicks = nowTicks
                                });
                            }
                            else
                            {
                                skipCache.Remove(item.Id);
                            }
                        }

                        if (alreadyComplete)
                        {
                            // Log WHY so users (esp. large libraries) can see skips aren't a no-op.
                            _logger.LogInformation(
                                "[{Current}/{Total}] Skipping {ItemName}: already satisfied (full={Full}, forced={Forced}, translated={Translated})",
                                completed + 1, allItems.Count, item.Name, hasFullSrt, hasForcedSrt, hasTranslatedSrt);
                            completed++;
                            queue.ReportTaskProgress(null, completed, allItems.Count, failed);
                            progress.Report((double)completed / allItems.Count * 100);
                            continue;
                        }
                    }
                }

                try
                {
                    _logger.LogInformation("[{Current}/{Total}] Processing {ItemName}",
                        completed + 1, allItems.Count, item.Name);
                    // Reset at item start so the bar reads 0 during audio extraction (before whisper
                    // runs). WhisperProvider also resets at each whisper run; both are idempotent.
                    queue.ResetFileProgress();
                    queue.ReportTaskProgress(item.Name, completed, allItems.Count, failed, itemType, libName);

                    await SubtitleQueueService.TranscriptionLock.WaitAsync(cancellationToken);
                    try
                    {
                        if (config.PauseOnPlayback)
                        {
                            await TranscribeWithPlaybackMonitorAsync(manager, item, provider, language, cancellationToken);
                        }
                        else
                        {
                            await manager.GenerateSubtitleAsync(item, provider, language, cancellationToken);
                        }
                    }
                    finally
                    {
                        SubtitleQueueService.TranscriptionLock.Release();
                    }
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
                queue.ReportTaskProgress(null, completed, allItems.Count, failed);
                progress.Report((double)completed / allItems.Count * 100);
            }

            queue.ReportTaskProgress(null, completed, allItems.Count, failed);
            queue.ReportTaskComplete();
            _logger.LogInformation("Subtitle generation task complete. Processed: {Processed}, Failed: {Failed}",
                completed, failed);
            }
            finally
            {
                // Persist even on cancellation / pause-timeout so the run keeps the progress it made.
                // Prune to the enumerated candidate set (not the reached set) so items not yet visited
                // this run keep their prior entry — the reason per-item state beats a global watermark.
                if (skipCache != null)
                {
                    skipCache.PruneTo(candidateIds);
                    skipCache.Save(cachePath, cacheSignature, _logger);
                }
            }
        }

        /// <summary>
        /// Issue #82: true if the item has at least one usable, non-forced subtitle stream in ANY
        /// language, excluding the plugin's own generated output. Used to refine the language- and
        /// type-blind <c>Video.HasSubtitles</c> so a forced-only (or, by default, image-only)
        /// embedded track no longer counts as a complete full subtitle. When
        /// <paramref name="requireText"/> is false (CountImageSubtitlesAsPresent on), image tracks
        /// count too — hence "stream", not "text", in the name. Language-agnostic on purpose (the
        /// full pass targets the audio languages, which may be auto-detected): we only filter out
        /// the forced/image false-positives here and let the per-language skip (SubtitleManager)
        /// make the precise per-language decision.
        /// </summary>
        private static bool HasUsableSubtitleStream(BaseItem item, bool ignoreForced, bool requireText = true)
        {
            // Reuse the shared usability predicate (non-forced, not our own output, text unless the
            // image toggle is on) so this any-language pre-filter never drifts from IsUsableStream.
            return SubtitleStreamReader.GetSubtitleStreams(item)
                .Any(s => SubtitleInventory.IsUsableStream(s, ignoreForced, requireText));
        }

        internal async Task WaitForPlaybackIdleAsync(CancellationToken cancellationToken)
        {
            bool logged = false;
            var deadline = DateTime.UtcNow.AddHours(4);
            var queue = SubtitleQueueService.Instance;
            while (_sessionManager.Sessions.Any(s => s.NowPlayingItem != null))
            {
                if (DateTime.UtcNow >= deadline)
                {
                    _logger.LogWarning("Playback still active after 4 hours — resuming subtitle generation to avoid indefinite stall");
                    break;
                }
                if (!logged)
                {
                    _logger.LogInformation("Active playback detected — pausing subtitle generation until idle");
                    queue.ReportPhase("Waiting for playback to stop");
                    logged = true;
                }
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
            }
            if (logged)
            {
                queue.ReportPhase(null!);
                _logger.LogInformation("Playback stopped — resuming subtitle generation");
            }
        }

        /// <summary>
        /// Runs transcription while monitoring for playback. If playback starts mid-transcription,
        /// cancels whisper (saving partial SRT), waits for playback to end, then retries.
        /// Resume logic in SubtitleManager picks up from where the partial SRT left off.
        /// </summary>
        private async Task TranscribeWithPlaybackMonitorAsync(
            SubtitleManager manager, BaseItem item, ISubtitleProvider provider,
            string language, CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                using var playbackCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var monitorTask = MonitorPlaybackAsync(playbackCts.Token);
                var transcribeTask = manager.GenerateSubtitleAsync(item, provider, language, playbackCts.Token);

                var finished = await Task.WhenAny(transcribeTask, monitorTask);

                if (finished == transcribeTask)
                {
                    // Transcription completed (or threw) before playback started — cancel monitor and propagate
                    await playbackCts.CancelAsync();
                    try { await monitorTask; } catch (OperationCanceledException) { }
                    await transcribeTask; // propagate exceptions
                    return;
                }

                // Playback detected — cancel the transcription (whisper saves partial SRT)
                _logger.LogInformation("Playback started during transcription of {ItemName} — interrupting", item.Name);
                await playbackCts.CancelAsync();

                try
                {
                    await transcribeTask;
                }
                catch (OperationCanceledException)
                {
                    // Expected — whisper was killed, partial SRT saved
                }

                // Wait for playback to finish, then retry (resume picks up from partial)
                await WaitForPlaybackIdleAsync(cancellationToken);
                _logger.LogInformation("Retrying transcription for {ItemName} (will resume from partial)", item.Name);
            }
        }

        /// <summary>
        /// Polls sessions every 10 seconds. Returns (completes) when playback is detected.
        /// </summary>
        private async Task MonitorPlaybackAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                if (_sessionManager.Sessions.Any(s => s.NowPlayingItem != null))
                {
                    return;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
