using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using WhisperSubs.Configuration;
using WhisperSubs.Providers;
using Microsoft.Extensions.Logging;

namespace WhisperSubs.Controller.Workers
{
    /// <summary>
    /// Builds the transcription worker pool from config (v4.0), backward-compatible via
    /// <see cref="WorkerPlan"/>: no config → one local worker (identical to today); a legacy single
    /// <c>RemoteWhisperApiUrl</c> → one remote worker (remote-only); an explicit <c>Workers</c> list →
    /// those + optionally the local host. Excluded from coverage — it news up providers from config, the
    /// same rationale as <see cref="SubtitleProviderFactory"/>. The composition decision (WorkerPlan) and
    /// the routing (WorkerScheduling) are the tested, pure parts.
    /// </summary>
    [ExcludeFromCodeCoverage(Justification = "Orchestration: constructs providers from config, like SubtitleProviderFactory")]
    public static class WorkerRegistry
    {
        public static IReadOnlyList<ITranscriptionWorker> BuildWorkers(PluginConfiguration config, ILoggerFactory loggerFactory)
        {
            var workers = new List<ITranscriptionWorker>();
            var plan = WorkerPlan.Decide(
                config.Workers?.Count ?? 0,
                !string.IsNullOrWhiteSpace(config.RemoteWhisperApiUrl),
                config.EnableLocalWorker);

            switch (plan.Source)
            {
                case WorkerSource.ExplicitList:
                    foreach (var w in config.Workers.Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.ApiUrl)))
                    {
                        workers.Add(BuildRemote(
                            id: string.IsNullOrWhiteSpace(w.Id) ? w.ApiUrl : w.Id,
                            name: string.IsNullOrWhiteSpace(w.Name) ? w.ApiUrl : w.Name,
                            url: w.ApiUrl,
                            key: w.ApiKey ?? string.Empty,
                            model: string.IsNullOrWhiteSpace(w.Model) ? config.RemoteWhisperModel : w.Model,
                            maxConcurrency: w.MaxConcurrency,
                            costWeight: w.CostWeight,
                            canTranslate: w.CanTranslate,
                            config: config,
                            loggerFactory: loggerFactory));
                    }
                    break;

                case WorkerSource.LegacyRemote:
                    // A pre-v4 single remote URL = the whole "remote" worker, remote-only.
                    workers.Add(BuildRemote(
                        id: "remote", name: "Remote",
                        url: config.RemoteWhisperApiUrl,
                        key: (config.RemoteWhisperApiKey ?? string.Empty).Trim(),
                        model: config.RemoteWhisperModel,
                        maxConcurrency: 1, costWeight: 0, canTranslate: true,
                        config: config, loggerFactory: loggerFactory));
                    break;
            }

            // Add the host's own local whisper unless the plan says otherwise. The fallback guarantees the
            // pool is never empty (e.g. an explicit list of all-disabled/blank workers with local off).
            if (plan.AddLocal || workers.Count == 0)
            {
                workers.Add(new TranscriptionWorker(
                    "local", "Local (this server)",
                    SubtitleProviderFactory.CreateLocal(config, loggerFactory),
                    new WorkerCapabilities { IsLocal = true, CostWeight = 0, MaxConcurrency = 1, CanTranslate = true }));
            }

            return workers;
        }

        private static ITranscriptionWorker BuildRemote(
            string id, string name, string url, string key, string model,
            int maxConcurrency, double costWeight, bool canTranslate,
            PluginConfiguration config, ILoggerFactory loggerFactory)
        {
            var resolvedModel = string.IsNullOrWhiteSpace(model) ? "Systran/faster-whisper-large-v3" : model.Trim();
            var provider = new RemoteWhisperProvider(
                loggerFactory.CreateLogger<RemoteWhisperProvider>(),
                url, resolvedModel, key,
                config.JobTimeoutRealtimeFactor, config.JobMinTimeoutSeconds, config.JobMaxTimeoutHours);

            return new TranscriptionWorker(id, name, provider, new WorkerCapabilities
            {
                IsLocal = false,
                CostWeight = costWeight,
                MaxConcurrency = maxConcurrency < 1 ? 1 : maxConcurrency,
                CanTranslate = canTranslate
            });
        }
    }
}
