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
    }
}
