using System;
using System.Text.RegularExpressions;

namespace WhisperSubs.Controller.Workers
{
    /// <summary>
    /// Pure, total sanitizer for text that came from an UNTRUSTED upstream endpoint (a worker's error
    /// response, an exception message, a configured URL) before it is shown to an admin or written to the
    /// Jellyfin log.
    /// <para>
    /// Why this exists: issue #138. A remote endpoint refused the upload and the admin saw only
    /// "HTTP 413" / "HTTP 400" — the provider's own explanation was captured and then discarded, so the
    /// failure was undiagnosable without debug logging. Echoing the upstream body fixes that, but the body
    /// is attacker-influenced text that may contain the admin's own API key (some gateways echo the request
    /// back), presigned URLs, control characters (log forging, CWE-117) or markup (XSS, CWE-79).
    /// </para>
    /// <para>
    /// SECURITY-REVIEW: this is the ONLY boundary at which an upstream body may cross into the UI or the log.
    /// The RAW body stays internal to RemoteApiException, because the response-format negotiation
    /// pattern-matches against it — sanitizing before that would regress the #138 negotiation.
    /// Order is load-bearing: redact BEFORE truncating, so a secret straddling the cut cannot be half-revealed.
    /// Callers must render the result as text (textContent), never as HTML.
    /// </para>
    /// </summary>
    public static class UpstreamErrorSanitizer
    {
        /// <summary>Default cap for a snippet shown in the config page or a log message.</summary>
        public const int DefaultMaxLength = 400;

        internal const string Redacted = "[redacted]";

        private const RegexOptions Opts =
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

        // A hostile body must never hang a transcription. Every pattern is bounded and fails CLOSED.
        private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(250);

        // Whole URL query strings. The single strongest rule: removes presigned-URL signatures
        // (X-Amz-Signature, Google Signature=, Azure sig=), ?api_key=, ?api-version=, ?token= at a stroke.
        private static readonly Regex UrlQuery = new(
            @"(?<url>[a-z][a-z0-9+.\-]*://[^\s""'<>]+?)\?[^\s""'<>]*", Opts, Budget);

        // URL userinfo: https://user:pass@host
        private static readonly Regex UrlUserInfo = new(
            @"(?<scheme>[a-z][a-z0-9+.\-]*://)[^/\s""'<>@]+@", Opts, Budget);

        private static readonly Regex AuthHeader = new(
            @"\b(?:proxy-)?authorization\b\s*[:=]\s*[""']?[^\r\n""',}]{4,}", Opts, Budget);

        private static readonly Regex BearerToken = new(
            @"\bbearer\s+[A-Za-z0-9._\-+/=]{8,}", Opts, Budget);

        // JWTs before the generic base64 rule, so the whole token goes rather than one segment.
        private static readonly Regex Jwt = new(
            @"\beyJ[A-Za-z0-9_\-]{6,}\.[A-Za-z0-9_\-]{6,}\.[A-Za-z0-9_\-]{4,}\b", Opts, Budget);

        // Provider-prefixed key families: sk-, sk-or-v1-, sk-proj-, gsk_ (Groq), xai-, ghp_, glpat-, hf_.
        private static readonly Regex PrefixedKey = new(
            @"\b(?:sk|rk|pk|gsk|xai|hf|ghp|gho|glpat)[-_][A-Za-z0-9_\-]{12,}", Opts, Budget);

        private static readonly Regex NamedSecret = new(
            @"\b(?<k>api[_\-]?key|apikey|access[_\-]?token|auth[_\-]?token|refresh[_\-]?token|" +
            @"id[_\-]?token|client[_\-]?secret|secret|password|passwd|pwd|token|signature|sig)\b" +
            @"\s*[""']?\s*[:=]\s*[""']?(?<v>[^\s""',&}\]]{4,})", Opts, Budget);

        // Long high-entropy runs: no English word is 40 chars, and a 32+ hex run is a key, hash or request
        // id. '/' is deliberately NOT in the class — including it made any long URL path match, so a
        // perfectly innocent endpoint path was redacted out of the message the admin needed to read.
        private static readonly Regex LongBase64 = new(@"\b[A-Za-z0-9+]{40,}={0,2}\b", Opts, Budget);
        private static readonly Regex LongHex = new(@"\b[0-9a-f]{32,}\b", Opts, Budget);

        private static readonly Regex Email = new(
            @"\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b", Opts, Budget);

        // Strips every Unicode "other" category in one rule: Cc (control, incl. CR/LF/TAB -> log forging),
        // Cf (zero-width, BiDi overrides -> spoofing), Cs (lone surrogates from a UTF-8 cut at the capture
        // bound), Co, Cn.
        private static readonly Regex ControlChars = new(@"\p{C}+", RegexOptions.Compiled, Budget);
        private static readonly Regex WhitespaceRun = new(@"\s{2,}", RegexOptions.Compiled, Budget);

        /// <summary>
        /// Redacts secrets from untrusted upstream text, flattens it to a single line, and caps its length.
        /// Never throws; returns an empty string for null/blank input.
        /// </summary>
        /// <param name="body">Raw upstream text. May be null, empty, or hostile.</param>
        /// <param name="apiKey">The worker's own configured key, for exact-match removal. May be null.</param>
        /// <param name="maxLength">Cap applied AFTER redaction.</param>
        public static string Sanitize(string? body, string? apiKey, int maxLength = DefaultMaxLength)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                return string.Empty;
            }

            if (maxLength < 16)
            {
                maxLength = 16;
            }

            var s = body;

            // STEP 1 - the admin's own key, exactly. Highest-value rule: the realistic leak is a gateway
            // reflecting the request (headers included) back in an error envelope. Guarded at length >= 8 so
            // a short or placeholder key cannot blank out the whole message.
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                var key = apiKey.Trim();
                if (key.Length >= 8)
                {
                    s = s.Replace(key, Redacted, StringComparison.Ordinal);
                    s = s.Replace(key, Redacted, StringComparison.OrdinalIgnoreCase);
                    s = s.Replace(Uri.EscapeDataString(key), Redacted, StringComparison.OrdinalIgnoreCase);
                }
            }

            // STEP 2 - pattern redaction. Order matters (URL query first; JWT before generic base64).
            try
            {
                s = UrlQuery.Replace(s, m => m.Groups["url"].Value + "?" + Redacted);
                s = UrlUserInfo.Replace(s, m => m.Groups["scheme"].Value + Redacted + "@");
                s = AuthHeader.Replace(s, "authorization: " + Redacted);
                s = BearerToken.Replace(s, "Bearer " + Redacted);
                s = Jwt.Replace(s, Redacted);
                s = PrefixedKey.Replace(s, Redacted);
                s = NamedSecret.Replace(s, m => m.Groups["k"].Value + "=" + Redacted);
                s = LongBase64.Replace(s, Redacted);
                s = LongHex.Replace(s, Redacted);
                s = Email.Replace(s, "[email]");

                // STEP 3 - flatten to one safe line. Inside the try: these carry the same regex budget, and
                // a timeout escaping here would propagate out past the caller's status handling.
                s = ControlChars.Replace(s, " ");
                s = WhitespaceRun.Replace(s, " ").Trim();
            }
            catch (RegexMatchTimeoutException)
            {
                // Fail CLOSED: a pathological body must never break diagnostics, and must never fall
                // through to the raw text.
                return "[error detail withheld: could not be sanitized]";
            }

            // STEP 4 - cap LAST, so truncation can never re-expose a partially redacted secret.
            if (s.Length > maxLength)
            {
                s = s[..(maxLength - 1)].TrimEnd() + "…";
            }

            return s;
        }

        /// <summary>
        /// Sanitizes an endpoint URL for display or logging: drops userinfo and the entire query string.
        /// An admin may legitimately paste a key into the URL (Azure-style "?api-version=", proxy
        /// "?api_key="); without this that string reaches jellyfin.log and the config page verbatim.
        /// <para>
        /// Scope, stated honestly: this covers the userinfo and query-string forms only. A credential
        /// embedded in the PATH (some gateways use /v1/&lt;account&gt;/…) is preserved, because the path is
        /// usually the single most useful thing in a "wrong endpoint" diagnosis and blanket-masking it
        /// would hide exactly what the admin needs.
        /// </para>
        /// </summary>
        /// <summary>
        /// Sanitizes a redirect Location for display, with the same rules as <see cref="SanitizeEndpoint"/>.
        /// The target is usually the diagnosis in one word — an SSO login page, the https:// twin of an
        /// http:// URL — but it is upstream-controlled and its query string routinely embeds the ORIGINAL
        /// request URL (rd=/redirect_uri=), which may carry a key the admin pasted there. Tolerates the
        /// relative form ("/login") a gateway may emit; empty when the response carried no Location at all.
        /// </summary>
        public static string SanitizeRedirectTarget(Uri? location)
            => location is null ? string.Empty : SanitizeEndpoint(location.OriginalString);

        public static string SanitizeEndpoint(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return string.Empty;
            }

            var trimmed = url.Trim();
            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            {
                return Sanitize(trimmed, apiKey: null, maxLength: 200);
            }

            // uri.Authority excludes userinfo by construction.
            var query = string.IsNullOrEmpty(uri.Query) ? string.Empty : "?" + Redacted;
            return uri.Scheme + "://" + uri.Authority + uri.AbsolutePath + query;
        }
    }
}
