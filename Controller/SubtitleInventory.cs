using System;
using System.Collections.Generic;

namespace WhisperSubs.Controller
{
    /// <summary>
    /// A lightweight, provider-agnostic description of one subtitle track on a media item.
    /// Decoupled from Jellyfin's MediaStream so the skip logic is pure and unit-testable.
    /// </summary>
    public sealed class SubtitleStreamInfo
    {
        /// <summary>Language tag as reported by the container/ffprobe (e.g. "eng", "en", "english"). May be null/empty.</summary>
        public string? Language { get; init; }

        /// <summary>True for external sidecar subtitle files; false for embedded streams.
        /// Informational: the current skip logic treats embedded and external equally; reserved
        /// in case a future toggle wants to count embedded vs external subtitle tracks differently.</summary>
        public bool IsExternal { get; init; }

        /// <summary>True for forced tracks (foreign-dialogue inserts only — not full dialogue).</summary>
        public bool IsForced { get; init; }

        /// <summary>True for text-based subtitles (SRT/ASS/VTT). False for image subs (PGS/VOBSUB/DVD).</summary>
        public bool IsTextSubtitle { get; init; }

        /// <summary>Sidecar file path for external subs; null/empty for embedded.</summary>
        public string? Path { get; init; }
    }

    /// <summary>
    /// Pure decision logic for "does this media already have a usable subtitle?" (issue #82).
    /// Operates on a list of <see cref="SubtitleStreamInfo"/> so it can be unit-tested without
    /// Jellyfin. A Jellyfin <c>BaseItem</c> is adapted into this shape by SubtitleStreamReader.
    /// </summary>
    public static class SubtitleInventory
    {
        /// <summary>
        /// Returns true if a subtitle in <paramref name="desiredLanguage"/> already exists and is
        /// "usable" — i.e. text-based and (when <paramref name="ignoreForced"/>) not forced. The
        /// plugin's OWN generated output is excluded (so it never self-satisfies a fresh request).
        /// </summary>
        /// <param name="streams">All subtitle streams on the item (embedded + external).</param>
        /// <param name="desiredLanguage">Wanted language; ISO 639-1 ("en"), 639-2 ("eng"), or word ("english").</param>
        /// <param name="ignoreForced">When true, forced tracks do not count as satisfying the need.</param>
        /// <param name="requireText">When true, image subs (PGS/VOBSUB) do not count.</param>
        public static bool HasUsableSubtitle(
            IEnumerable<SubtitleStreamInfo>? streams,
            string desiredLanguage,
            bool ignoreForced = true,
            bool requireText = true)
        {
            if (streams == null) return false;
            var wanted = NormalizeLang(desiredLanguage);
            if (wanted == null) return false;

            foreach (var s in streams)
            {
                if (!IsUsableStream(s, ignoreForced, requireText)) continue;
                if (NormalizeLang(s!.Language) == wanted) return true;
            }
            return false;
        }

        /// <summary>
        /// Single source of truth for "is this subtitle stream a usable candidate?" — text-based
        /// (when <paramref name="requireText"/>), not forced (when <paramref name="ignoreForced"/>),
        /// and not the plugin's own generated output. Language-agnostic; callers add the language
        /// match. Shared by <see cref="HasUsableSubtitle"/> and the scheduled task's any-language
        /// pre-filter so the usability rule never drifts between them.
        /// </summary>
        public static bool IsUsableStream(SubtitleStreamInfo? s, bool ignoreForced = true, bool requireText = true)
        {
            if (s == null) return false;
            if (ignoreForced && s.IsForced) return false;
            if (requireText && !s.IsTextSubtitle) return false;
            if (IsPluginGeneratedPath(s.Path)) return false;     // don't let our own output satisfy us
            return true;
        }

        /// <summary>
        /// True if the path is one of the plugin's own generated outputs
        /// (<c>.generated.srt</c>, <c>.forced.generated.srt</c>, <c>.en.translated.srt</c>),
        /// which must not count as a pre-existing user subtitle.
        /// </summary>
        public static bool IsPluginGeneratedPath(string? path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var name = System.IO.Path.GetFileName(path);
            return name.Contains(".generated.", StringComparison.OrdinalIgnoreCase)
                || name.Contains(".translated.", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Normalizes a language tag (639-2/B, 639-2/T, full English word, or already-639-1) to a
        /// canonical ISO 639-1 code for comparison. Returns null for empty/undetermined ("und").
        /// Mirrors SubtitleManager.NormalizeLanguageCode plus common English-word spellings.
        /// </summary>
        public static string? NormalizeLang(string? code)
        {
            if (string.IsNullOrWhiteSpace(code)) return null;
            var c = code.Trim().ToLowerInvariant();
            // Undetermined / placeholder language tags never match a concrete desired language.
            if (c == "und" || c == "unknown" || c == "auto") return null;
            return c switch
            {
                "spa" or "spanish" or "es" => "es",
                "eng" or "english" or "en" => "en",
                "fra" or "fre" or "french" or "fr" => "fr",
                "deu" or "ger" or "german" or "de" => "de",
                "ita" or "italian" or "it" => "it",
                "por" or "portuguese" or "pt" => "pt",
                "rus" or "russian" or "ru" => "ru",
                "jpn" or "japanese" or "ja" => "ja",
                "zho" or "chi" or "chinese" or "zh" => "zh",
                "kor" or "korean" or "ko" => "ko",
                "ara" or "arabic" or "ar" => "ar",
                "hin" or "hindi" or "hi" => "hi",
                "pol" or "polish" or "pl" => "pl",
                "nld" or "dut" or "dutch" or "nl" => "nl",
                "tur" or "turkish" or "tr" => "tr",
                "swe" or "swedish" or "sv" => "sv",
                "dan" or "danish" or "da" => "da",
                "fin" or "finnish" or "fi" => "fi",
                "nor" or "norwegian" or "no" => "no",
                "ces" or "cze" or "czech" or "cs" => "cs",
                "ron" or "rum" or "romanian" or "ro" => "ro",
                "hun" or "hungarian" or "hu" => "hu",
                "ell" or "gre" or "greek" or "el" => "el",
                "heb" or "hebrew" or "he" => "he",
                "tha" or "thai" or "th" => "th",
                "ukr" or "ukrainian" or "uk" => "uk",
                "vie" or "vietnamese" or "vi" => "vi",
                "ind" or "indonesian" or "id" => "id",
                "cat" or "catalan" or "ca" => "ca",
                "eus" or "baq" or "basque" or "eu" => "eu",
                "glg" or "galician" or "gl" => "gl",
                // Region-qualified (e.g. "pt-BR", "en-US") → base language.
                _ when c.Contains('-') => NormalizeLang(c.Split('-')[0]),
                _ => c
            };
        }
    }
}
