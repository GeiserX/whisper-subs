using System;
using System.Collections.Generic;
using System.Linq;
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

        /// <summary>
        /// Normalizes an endpoint URL for cross-row duplicate comparison. Delegates to the single canonical
        /// implementation in <see cref="WorkerEndpointDedup.NormalizeEndpoint"/> so this advisory check and
        /// the pool's actual same-endpoint collapse agree on what "the same endpoint" means.
        /// </summary>
        internal static string NormalizeEndpoint(string apiUrl) => WorkerEndpointDedup.NormalizeEndpoint(apiUrl);

        /// <summary>
        /// Cross-row check (advisory, not a hard error): flags groups of 2+ ENABLED worker rows that
        /// normalize to the SAME physical endpoint (see <see cref="NormalizeEndpoint"/>). whisper.cpp's
        /// <c>whisper-server</c> serves one request at a time, so pointing two enabled rows at it (or
        /// otherwise oversizing <c>MaxConcurrency</c> for it) oversubscribes that single process and causes
        /// request pile-up/timeouts. Disabled rows are ignored. Returns one warning message per duplicated
        /// endpoint group; an empty list means no duplicates.
        /// </summary>
        public static IReadOnlyList<string> CheckDuplicateEndpoints(IEnumerable<WhisperWorker>? workers)
        {
            if (workers is null)
                return Array.Empty<string>();

            return workers
                .Where(w => w is not null && w.Enabled && !string.IsNullOrWhiteSpace(w.ApiUrl))
                .GroupBy(w => NormalizeEndpoint(w.ApiUrl))
                .Where(g => g.Count() >= 2)
                .Select(g =>
                    $"{g.Count()} enabled workers share the same endpoint ({g.Key}). A whisper-server " +
                    "instance handles one request at a time, so duplicate rows (or an inflated max " +
                    "concurrency) on the same endpoint will oversubscribe it and cause request " +
                    "pile-up/timeouts.")
                .ToList();
        }
    }
}
