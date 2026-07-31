using System;
using System.Collections.Generic;
using System.Linq;
using WhisperSubs.Configuration;

namespace WhisperSubs.Controller.Workers
{
    /// <summary>
    /// Pure helpers that collapse configured worker rows pointing at the SAME physical endpoint into a
    /// single pool worker. whisper.cpp's <c>whisper-server</c> serves ONE request at a time, so two enabled
    /// rows — or an accidental duplicate — for one URL would hand the pool two independent concurrency
    /// slots and oversubscribe that single backend: a short language-detection probe then queues behind a
    /// long transcription on it and blows its per-call deadline (the v4.3.0 flakiness). Collapsing here makes
    /// each physical endpoint exactly one worker, so a worker's <c>MaxConcurrency</c> and the
    /// ΣMaxConcurrency backpressure gate reflect the backend's real request capacity. Pure + unit-tested;
    /// <see cref="WorkerRegistry"/> (the coverage-excluded orchestration) just calls it.
    /// </summary>
    public static class WorkerEndpointDedup
    {
        /// <summary>
        /// Canonical comparison key for a worker endpoint URL: lowercased scheme+host, explicit-or-default
        /// port, and path with any single trailing slash removed. So <c>http://Host:9010</c>,
        /// <c>http://host:9010/</c>, and <c>HTTP://host:9010</c> all compare equal, while a different
        /// host/port/path does not. Non-absolute or unparseable input falls back to a trimmed, lowercased,
        /// trailing-slash-stripped string so the key is still stable and this never throws.
        /// </summary>
        public static string NormalizeEndpoint(string apiUrl)
        {
            var s = (apiUrl ?? string.Empty).Trim();
            if (s.Length == 0)
            {
                return string.Empty;
            }

            if (Uri.TryCreate(s, UriKind.Absolute, out var u))
            {
                var scheme = u.Scheme.ToLowerInvariant();
                var host = u.Host.ToLowerInvariant();
                var port = u.IsDefaultPort ? DefaultPort(scheme) : u.Port;
                var path = u.AbsolutePath;
                if (path.Length > 1 && path.EndsWith("/", StringComparison.Ordinal))
                {
                    path = path.TrimEnd('/');
                }
                if (path == "/")
                {
                    path = string.Empty;
                }

                return port >= 0
                    ? string.Format("{0}://{1}:{2}{3}", scheme, host, port, path)
                    : string.Format("{0}://{1}{2}", scheme, host, path);
            }

            var t = s.ToLowerInvariant();
            if (t.Length > 1 && t.EndsWith("/", StringComparison.Ordinal))
            {
                t = t.TrimEnd('/');
            }
            return t;
        }

        private static int DefaultPort(string scheme) => scheme switch
        {
            "http" => 80,
            "https" => 443,
            _ => -1,
        };

        /// <summary>
        /// Collapse rows sharing a normalized endpoint into one (the first in iteration order keeps its
        /// identity), taking the MIN <c>MaxConcurrency</c> across the group so a duplicate can never WIDEN
        /// the physical server's real capacity. Rows are returned in first-seen order. Input is expected to
        /// be pre-filtered to enabled, non-blank-URL rows; a blank/unnormalizable URL is passed through
        /// unchanged (never grouped under the empty key). Never mutates the input rows.
        /// </summary>
        public static IReadOnlyList<WhisperWorker> CollapseByEndpoint(IEnumerable<WhisperWorker> rows)
        {
            var byKey = new Dictionary<string, WhisperWorker>(StringComparer.Ordinal);
            var order = new List<string>();
            var passthrough = new List<WhisperWorker>();

            foreach (var w in rows ?? Enumerable.Empty<WhisperWorker>())
            {
                if (w is null)
                {
                    continue;
                }

                var key = NormalizeEndpoint(w.ApiUrl);
                if (key.Length == 0)
                {
                    passthrough.Add(Clone(w));
                    continue;
                }

                if (byKey.TryGetValue(key, out var kept))
                {
                    // Duplicate endpoint: keep the first row's identity, but never let the duplicate WIDEN
                    // the single backend's real concurrency — take the minimum of the two.
                    kept.MaxConcurrency = Math.Min(Clamp(kept.MaxConcurrency), Clamp(w.MaxConcurrency));
                }
                else
                {
                    var copy = Clone(w);
                    copy.MaxConcurrency = Clamp(copy.MaxConcurrency);
                    byKey[key] = copy;
                    order.Add(key);
                }
            }

            var result = order.Select(k => byKey[k]).ToList();
            result.AddRange(passthrough);
            return result;
        }

        private static int Clamp(int concurrency) => concurrency < 1 ? 1 : concurrency;

        /// <summary>
        /// Copies a configured row. <b>Every</b> property of <see cref="WhisperWorker"/> must be carried
        /// across: <c>WorkerRegistry.BuildWorkers</c> reads each worker's settings off this copy, so anything
        /// omitted here is silently reset to its default for every worker on the system — the admin's
        /// configuration is discarded with no error anywhere.
        /// <para>
        /// That is not theoretical: <c>MaxUploadBytes</c> and <c>UploadCodec</c> were added in 4.5.0.0 and
        /// missed here, so a worker set to Opus silently uploaded raw WAV (issue #138). If you add a property
        /// to <see cref="WhisperWorker"/>, add it here too — <c>WorkerCloneCompletenessTests</c> fails by
        /// reflection if you forget.
        /// </para>
        /// </summary>
        private static WhisperWorker Clone(WhisperWorker w) => new WhisperWorker
        {
            Id = w.Id,
            Name = w.Name,
            Enabled = w.Enabled,
            ApiUrl = w.ApiUrl,
            ApiKey = w.ApiKey,
            Model = w.Model,
            MaxConcurrency = w.MaxConcurrency,
            CostWeight = w.CostWeight,
            CanTranslate = w.CanTranslate,
            MaxUploadBytes = w.MaxUploadBytes,
            UploadCodec = w.UploadCodec,
        };
    }
}
