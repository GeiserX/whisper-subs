using System;
using System.Linq;
using WhisperSubs.Setup;

namespace WhisperSubs.Controller
{
    /// <summary>Pure, unit-tested input validation for the user-request path (#112).</summary>
    public static class RequestValidation
    {
        /// <summary>
        /// Accepts "auto" or an ISO 639-1 two-letter code and returns it normalised (lowercase), or null
        /// if unsupported. This is a security guard: the language flows into an output filename
        /// (<c>.&lt;lang&gt;.generated.srt</c>), so a value containing path separators or "…" must never
        /// reach the queue. An empty/whitespace input falls back to <paramref name="fallback"/>.
        /// </summary>
        public static string? NormalizeLanguage(string? language, string fallback)
        {
            var lang = string.IsNullOrWhiteSpace(language) ? fallback : language.Trim();
            if (string.IsNullOrWhiteSpace(lang)) return null;
            if (string.Equals(lang, "auto", StringComparison.OrdinalIgnoreCase)) return "auto";
            if (lang.Length == 2)
            {
                // ASCII a-z only. char.IsLetter is Unicode-aware (would accept e.g. "ññ"); constraining to
                // ASCII keeps the value that flows into the .<lang>.generated.srt filename strictly safe.
                var lower = lang.ToLowerInvariant();
                if (lower[0] >= 'a' && lower[0] <= 'z' && lower[1] >= 'a' && lower[1] <= 'z')
                {
                    return lower;
                }
            }
            return null;
        }

        /// <summary>
        /// Accepts "en" or one of the 24 Canary targets (any case, surrounding spaces ignored) and returns
        /// the catalog's own code string, never the input: the target ends up in a file name
        /// (<c>.&lt;lang&gt;.translated.srt</c>). Null for anything else, including "auto".
        /// </summary>
        public static string? NormalizeTranslationTarget(string? target)
        {
            if (string.IsNullOrWhiteSpace(target)) return null;
            var t = target.Trim();
            if (string.Equals(t, "en", StringComparison.OrdinalIgnoreCase)) return "en";
            return CanaryCatalog.Targets.FirstOrDefault(c => string.Equals(c.Code, t, StringComparison.OrdinalIgnoreCase))?.Code;
        }

        /// <summary>
        /// A translation request reads the source language from the audio, so the only language it may
        /// carry is "auto" or nothing.
        /// </summary>
        public static bool IsTranslationLanguageAllowed(string? language)
            => string.IsNullOrWhiteSpace(language) || string.Equals(language.Trim(), "auto", StringComparison.OrdinalIgnoreCase);
    }
}
