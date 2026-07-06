using System;
using WhisperSubs.Configuration;

namespace WhisperSubs.Controller.Workers
{
    /// <summary>
    /// Pure validation for a configured <see cref="WhisperWorker"/> row (v4.0 config UI). Returns the first
    /// problem (or ok) so the config page and the Test-connection endpoint can reject a malformed worker
    /// before it ever reaches the pool. Kept free of Jellyfin/HTTP types so it is unit-testable, mirroring
    /// the codebase's other pure decision helpers.
    /// </summary>
    public static class WorkerConfigValidation
    {
        /// <summary>Validates a worker row. <c>Ok=false</c> carries a user-facing reason (first failure wins).</summary>
        public static (bool Ok, string? Error) Validate(WhisperWorker worker)
        {
            if (worker is null)
                return (false, "Worker is missing.");
            if (string.IsNullOrWhiteSpace(worker.ApiUrl))
                return (false, "Endpoint URL is required.");
            if (!Uri.TryCreate(worker.ApiUrl.Trim(), UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return (false, "Endpoint must be an absolute http(s) URL.");
            if (worker.MaxConcurrency < 1)
                return (false, "Max concurrency must be at least 1.");
            if (worker.CostWeight < 0)
                return (false, "Cost weight cannot be negative.");
            return (true, null);
        }
    }
}
