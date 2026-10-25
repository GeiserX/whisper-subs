using System.Linq;

namespace WhisperSubs.Controller.Workers
{
    /// <summary>Which set of workers the pool should be built from (backward-compatibility).</summary>
    public enum WorkerSource
    {
        /// <summary>The admin configured an explicit <c>Workers</c> list.</summary>
        ExplicitList,
        /// <summary>No list, but a legacy single <c>RemoteWhisperApiUrl</c> is set — remote-only, as pre-v4.</summary>
        LegacyRemote,
        /// <summary>Nothing configured — the host's own local whisper only (today's default).</summary>
        LocalOnly
    }

    /// <summary>
    /// Pure backward-compatibility decision for composing the worker pool (v4.0). Guarantees that a normal
    /// single-server install with no new config behaves EXACTLY as today, and that an existing legacy
    /// single-remote-URL install stays remote-only (the host's local whisper is not silently activated — a
    /// behaviour change that would otherwise surprise an upgrading remote-offload user).
    /// </summary>
    public static class WorkerPlan
    {
        /// <summary>
        /// Decide the worker composition. An explicit <c>Workers</c> list wins, plus the local worker when
        /// <paramref name="enableLocalWorker"/>. Otherwise a legacy <c>RemoteWhisperApiUrl</c> means
        /// remote-ONLY (local not added, exactly the pre-v4 behaviour). Otherwise: local only.
        /// </summary>
        public static (WorkerSource Source, bool AddLocal) Decide(
            int explicitWorkerCount, bool hasLegacyRemoteUrl, bool enableLocalWorker)
        {
            if (explicitWorkerCount > 0) return (WorkerSource.ExplicitList, enableLocalWorker);
            if (hasLegacyRemoteUrl) return (WorkerSource.LegacyRemote, false);
            return (WorkerSource.LocalOnly, true);
        }

        /// <summary>
        /// Whether the built pool holds the host's own worker: the plan adds it, or no remote worker was
        /// built (the pool is never empty, e.g. every explicit row disabled). The one rule for
        /// <see cref="WorkerRegistry.BuildWorkers"/> and for the translation error that explains why this
        /// server cannot serve a target.
        /// </summary>
        public static bool IncludesLocal(bool addLocal, int builtRemoteWorkers) => addLocal || builtRemoteWorkers == 0;

        /// <summary>
        /// Whether the host's own worker may join a pool that also holds <paramref name="builtRemoteWorkers"/>
        /// other workers: always when it has an engine, and never without one while another worker exists. A
        /// host with nothing installed would otherwise win every tie against an idle akou row and fail the
        /// title. Alone, it stays, so its "download a model" error is what the admin sees. Pure.
        /// </summary>
        public static bool LocalUsable(bool hasEngine, int builtRemoteWorkers) => hasEngine || builtRemoteWorkers == 0;

        /// <summary>
        /// Whether the configuration has anything that transcribes, so the nightly task has a reason to run:
        /// a local model path, the legacy remote URL, or an enabled worker row with a URL (an akou-only
        /// install sets no model path at all). Pure.
        /// </summary>
        public static bool HasTranscriber(WhisperSubs.Configuration.PluginConfiguration config)
            => !string.IsNullOrWhiteSpace(config.WhisperModelPath)
               || !string.IsNullOrWhiteSpace(config.RemoteWhisperApiUrl)
               || (config.Workers?.Any(w => w.Enabled && !string.IsNullOrWhiteSpace(w.ApiUrl)) ?? false);

        /// <summary>
        /// <see cref="IncludesLocal"/> from config counts: <paramref name="usableExplicitRows"/> is the number
        /// of enabled explicit rows with a URL, which is what the registry builds remote workers from.
        /// </summary>
        /// <param name="localHasEngine">Whether the host has an engine (<see cref="LocalUsable"/>); true keeps the
        /// rule as it was for callers that do not know.</param>
        public static bool HostsLocal(int explicitWorkerCount, int usableExplicitRows, bool hasLegacyRemoteUrl, bool enableLocalWorker,
            bool localHasEngine = true)
        {
            var (source, addLocal) = Decide(explicitWorkerCount, hasLegacyRemoteUrl, enableLocalWorker);
            var remotes = source switch
            {
                WorkerSource.ExplicitList => usableExplicitRows,
                WorkerSource.LegacyRemote => 1,
                _ => 0,
            };
            return IncludesLocal(addLocal, remotes) && LocalUsable(localHasEngine, remotes);
        }
    }
}
