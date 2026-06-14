using Microsoft.Extensions.Logging;
using WhisperSubs.Configuration;
using WhisperSubs.Setup;

namespace WhisperSubs.Providers
{
    internal static class SubtitleProviderFactory
    {
        public static ISubtitleProvider Create(PluginConfiguration config, ILoggerFactory loggerFactory)
        {
            if (!string.IsNullOrWhiteSpace(config.RemoteWhisperApiUrl))
            {
                var model = string.IsNullOrWhiteSpace(config.RemoteWhisperModel)
                    ? "Systran/faster-whisper-large-v3"
                    : config.RemoteWhisperModel.Trim();
                var apiKey = (config.RemoteWhisperApiKey ?? string.Empty).Trim();
                return new RemoteWhisperProvider(
                    loggerFactory.CreateLogger<RemoteWhisperProvider>(),
                    config.RemoteWhisperApiUrl,
                    model,
                    apiKey);
            }

            // Resolve the Silero VAD model path when native VAD is enabled. Empty => VAD off
            // (the provider only adds --vad when given an existing model file).
            var vadModelPath = "";
            if (config.EnableVad)
            {
                var setup = new WhisperSetupService(
                    loggerFactory.CreateLogger<WhisperSetupService>(),
                    Plugin.Instance?.DataFolderPath ?? "");
                vadModelPath = setup.ResolveVadModelPath(config.VadModelPath) ?? "";

                // Auto-fetch the tiny (~865 KB) Silero model in the background if missing, so VAD
                // works without manual setup. TryAcquire no-ops if another download is running;
                // this run proceeds without --vad and subsequent runs pick the model up.
                if (string.IsNullOrEmpty(vadModelPath)
                    && WhisperSetupService.TryAcquire("vad", "Downloading Silero VAD model..."))
                {
                    var logger = loggerFactory.CreateLogger<WhisperSetupService>();
                    _ = System.Threading.Tasks.Task.Run(async () =>
                    {
                        try { await setup.DownloadVadModelAsync(System.Threading.CancellationToken.None); }
                        catch (System.Exception ex) { logger.LogWarning(ex, "Background VAD model download failed"); }
                    });
                }
            }

            return new WhisperProvider(
                loggerFactory.CreateLogger<WhisperProvider>(),
                config.WhisperModelPath,
                config.WhisperBinaryPath,
                config.WhisperThreadCount,
                config.CustomWhisperArgs,
                vadModelPath);
        }
    }
}
