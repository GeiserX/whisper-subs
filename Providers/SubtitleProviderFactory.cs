using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using WhisperSubs.Configuration;
using WhisperSubs.Setup;

namespace WhisperSubs.Providers
{
    internal static class SubtitleProviderFactory
    {
        /// <summary>
        /// Builds the host's local in-process whisper-cli provider (VAD + detection-model resolution).
        /// The v4.0 worker pool (WorkerRegistry) constructs the local worker with this directly; remote
        /// workers get their own RemoteWhisperProvider per configured endpoint in WorkerRegistry.
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "Orchestration: news up WhisperProvider + WhisperSetupService and depends on Plugin.Instance, File.Exists and TryAcquire — not unit-testable, same rationale as the excluded download/process methods.")]
        public static ISubtitleProvider CreateLocal(PluginConfiguration config, ILoggerFactory loggerFactory)
        {
            var setup = new WhisperSetupService(
                loggerFactory.CreateLogger<WhisperSetupService>(),
                Plugin.Instance?.DataFolderPath ?? "");

            // Resolve the Silero VAD model path when native VAD is enabled. Empty => VAD off
            // (the provider only adds --vad when given an existing model file).
            var vadModelPath = "";
            if (config.EnableVad)
            {
                vadModelPath = setup.ResolveVadModelPath(config.VadModelPath, config.VadModelVersion) ?? "";

                // Auto-fetch the tiny (~865 KB) selected Silero model in the background when IT is
                // missing — keyed on the selected version's own file, not on whether resolve returned
                // something, so choosing a new version downloads it even while an already-present older
                // model is used as a graceful fallback this run. Skipped when the user pointed at a
                // genuine external custom model (nothing for us to fetch). TryAcquire no-ops if another
                // download is running; subsequent runs pick the model up. (Issues #78/#105.)
                var selectedModelPath = setup.VadModelPathFor(ModelCatalog.ResolveVadModel(config.VadModelVersion).FileName);
                var usingExternalModel = !string.IsNullOrEmpty(vadModelPath)
                    && !WhisperSetupService.IsManagedVadPath(vadModelPath, setup.VadDirectory);
                if (!usingExternalModel && !System.IO.File.Exists(selectedModelPath)
                    && WhisperSetupService.TryAcquire("vad", "Downloading Silero VAD model..."))
                {
                    var logger = loggerFactory.CreateLogger<WhisperSetupService>();
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        try { await setup.DownloadVadModelAsync(config.VadModelVersion, System.Threading.CancellationToken.None); }
                        catch (System.Exception ex) { logger.LogWarning(ex, "Background VAD model download failed"); }
                    });
                }
            }

            // Always hand the provider the dedicated detection-model location (existence is checked
            // live there). When missing, auto-fetch ggml-base.bin (~148 MB) in the background so
            // forced-mode per-chunk language detection runs on a small, fast model instead of the
            // full transcription model — which times out on slow/no-AVX2 CPUs. Until it lands,
            // detection falls back to the transcription model, preserving legacy behavior. (Issue #95.)
            var detectionModelPath = setup.DetectionModelPath;
            if (!System.IO.File.Exists(detectionModelPath)
                && WhisperSetupService.TryAcquire("detect", "Downloading language-detection model..."))
            {
                var logger = loggerFactory.CreateLogger<WhisperSetupService>();
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    try { await setup.DownloadDetectionModelAsync(System.Threading.CancellationToken.None); }
                    catch (System.Exception ex) { logger.LogWarning(ex, "Background detection model download failed"); }
                });
            }

            var vadTuning = BuildVadTuning(config);

            return new WhisperProvider(
                loggerFactory.CreateLogger<WhisperProvider>(),
                config.WhisperModelPath,
                config.WhisperBinaryPath,
                config.WhisperThreadCount,
                config.CustomWhisperArgs,
                vadModelPath,
                detectionModelPath,
                vadTuning,
                config.SubtitleMaxLineLength);
        }

        /// <summary>
        /// Builds the Canary provider for the non-English translation targets, or null when the
        /// crispasr binary or the Canary model is not on disk. Not a pool worker: the translation pass
        /// uses it directly for Canary routes. The Silero VAD model is resolved even when
        /// <see cref="PluginConfiguration.EnableVad"/> is off, because Canary without VAD returns the
        /// whole file as one cue; the provider reports a missing model as a clear error.
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "Orchestration: depends on Plugin.Instance and File.Exists; the argument vector and route are unit-tested")]
        public static CanaryProvider? CreateCanary(PluginConfiguration config, ILogger logger)
        {
            if (!IsCanaryInstalled(config.CrispAsrBinaryPath, config.CanaryModelPath, System.IO.File.Exists)) return null;

            var dataPath = Plugin.Instance?.DataFolderPath ?? "";
            var vadModelPath = new WhisperSetupService(logger, dataPath)
                .ResolveVadModelPath(config.VadModelPath, config.VadModelVersion) ?? "";

            return new CanaryProvider(
                logger,
                config.CrispAsrBinaryPath,
                config.CanaryModelPath,
                config.CrispAsrThreadCount,
                vadModelPath,
                BuildVadTuning(config),
                config.SubtitleMaxLineLength,
                new CrispAsrSetupService(logger, dataPath).CacheDirectory);
        }

        /// <summary>Canary is available when both the configured binary and model files exist. Pure.</summary>
        internal static bool IsCanaryInstalled(string? binaryPath, string? modelPath, System.Func<string, bool> fileExists)
            => !string.IsNullOrWhiteSpace(binaryPath) && !string.IsNullOrWhiteSpace(modelPath)
               && fileExists(binaryPath) && fileExists(modelPath);

        /// <summary>
        /// Maps the plugin's VAD tuning config fields onto a <see cref="VadTuning"/>. Extracted from
        /// <see cref="CreateLocal"/> (excluded from coverage as untestable orchestration) so the field-by-field
        /// mapping stays pure and unit-testable — a guard against a silent field transposition. (Issue #105.)
        /// </summary>
        internal static VadTuning BuildVadTuning(PluginConfiguration config)
            => new VadTuning(
                config.VadThreshold,
                config.VadMinSpeechDurationMs,
                config.VadMinSilenceDurationMs,
                config.VadMaxSpeechDurationS,
                config.VadSpeechPadMs,
                config.VadSamplesOverlap);
    }
}
