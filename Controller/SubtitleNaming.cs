using System;
using System.Collections.Generic;
using System.IO;

namespace WhisperSubs.Controller
{
    /// <summary>
    /// Pure engine for configurable subtitle filename naming. Expands a user-configurable template
    /// into a base filename and recognises the plugin's own subtitle sidecars (new label-based names
    /// AND the legacy <c>.generated.</c>/<c>.translated.</c> anchors, for backward compatibility).
    ///
    /// BRAND-FIRST default: <c>{name}.{lang}.{label}{.type}</c> puts the label right after the language,
    /// so Jellyfin's picker Title (which hard-codes the filename Title first) shows the brand first.
    /// The <c>generated</c> token is dropped from new names — <c>{label}</c> is BOTH the picker Title
    /// and the ownership marker.
    ///
    /// No <c>Plugin.Instance</c>, no I/O — every member is a deterministic function of its arguments.
    /// </summary>
    internal static class SubtitleNaming
    {
        /// <summary>Default filename template. Brand-first so the label becomes the picker Title.</summary>
        internal const string DefaultTemplate = "{name}.{lang}.{label}{.type}";

        /// <summary>Default label token — also the ownership marker substring (<c>.WhisperSubs.</c>).</summary>
        internal const string DefaultLabel = "WhisperSubs";

        /// <summary>
        /// Expand <paramref name="template"/> into a base filename WITHOUT extension.
        /// <c>{name}</c>/<c>{lang}</c>/<c>{label}</c> are literal substitutions; <c>{name}</c> is OPAQUE —
        /// its dots (release tags) are never treated as separators. <c>{.type}</c> → <c>"." + type</c>
        /// when <paramref name="type"/> is non-empty, else <c>""</c>; <c>{type}</c> → the bare type or <c>""</c>.
        /// A final pass collapses any <c>".."</c> → <c>"."</c> and trims a leading/trailing <c>"."</c> so an
        /// empty segment never yields <c>Movie..srt</c> / <c>Movie.srt.</c> / <c>.Movie</c>.
        /// </summary>
        internal static string BuildBaseName(string template, string name, string lang, string label, string type)
        {
            var typeDot = string.IsNullOrEmpty(type) ? string.Empty : "." + type;
            var typeBare = type ?? string.Empty;

            var result = (template ?? string.Empty)
                .Replace("{name}", name ?? string.Empty, StringComparison.Ordinal)
                .Replace("{lang}", lang ?? string.Empty, StringComparison.Ordinal)
                .Replace("{label}", label ?? string.Empty, StringComparison.Ordinal)
                .Replace("{.type}", typeDot, StringComparison.Ordinal)
                .Replace("{type}", typeBare, StringComparison.Ordinal);

            // Collapse empty segments left by an absent token and trim any leading/trailing dot.
            while (result.Contains("..", StringComparison.Ordinal))
            {
                result = result.Replace("..", ".", StringComparison.Ordinal);
            }

            return result.Trim('.');
        }

        /// <summary>
        /// Writers' convenience: the directory of <paramref name="mediaPath"/> combined with
        /// <see cref="BuildBaseName"/> (over the media's filename-without-extension) plus
        /// <paramref name="extension"/> (which already includes the dot, e.g. <c>".srt"</c>).
        /// Returns the media-ADJACENT path; the caller still wraps it in <c>ResolveSubtitleSavePath</c>.
        /// </summary>
        internal static string BuildMediaAdjacentPath(string mediaPath, string template, string lang, string label, string type, string extension)
        {
            var dir = Path.GetDirectoryName(mediaPath) ?? string.Empty;
            var name = Path.GetFileNameWithoutExtension(mediaPath);
            var baseName = BuildBaseName(template, name, lang, label, type);
            return Path.Combine(dir, baseName + extension);
        }

        /// <summary>
        /// True when <paramref name="fileName"/> is one of the plugin's own subtitle sidecars: it contains
        /// <c>.{label}.</c> (new naming) OR the legacy <c>.generated.</c> / <c>.translated.</c> anchors.
        /// Case-insensitive. The dot before the extension supplies the trailing dot for the tail case, e.g.
        /// <c>Movie.en.WhisperSubs.srt</c> contains <c>.WhisperSubs.</c>.
        /// </summary>
        internal static bool IsPluginOwnedSubtitle(string fileName, string label)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return false;
            }

            if (fileName.Contains(".generated.", StringComparison.OrdinalIgnoreCase) ||
                fileName.Contains(".translated.", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return !string.IsNullOrEmpty(label) &&
                fileName.Contains("." + label + ".", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The kind of plugin-owned subtitle a filename represents.</summary>
        internal enum OwnedKind
        {
            /// <summary>Not a plugin-owned subtitle.</summary>
            None,

            /// <summary>A full-transcription subtitle.</summary>
            Full,

            /// <summary>A forced (foreign-dialogue) subtitle.</summary>
            Forced,

            /// <summary>An English translation subtitle.</summary>
            Translated,
        }

        /// <summary>
        /// Classify <paramref name="fileName"/>: <c>.forced.</c> → <see cref="OwnedKind.Forced"/>,
        /// <c>.translated.</c> → <see cref="OwnedKind.Translated"/>, otherwise a plugin-owned file
        /// → <see cref="OwnedKind.Full"/>, and a non-owned file → <see cref="OwnedKind.None"/>.
        /// </summary>
        internal static OwnedKind Classify(string fileName, string label)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                return OwnedKind.None;
            }

            if (fileName.Contains(".forced.", StringComparison.OrdinalIgnoreCase))
            {
                return OwnedKind.Forced;
            }

            if (fileName.Contains(".translated.", StringComparison.OrdinalIgnoreCase))
            {
                return OwnedKind.Translated;
            }

            return IsPluginOwnedSubtitle(fileName, label) ? OwnedKind.Full : OwnedKind.None;
        }

        /// <summary>
        /// Validate a template. It MUST contain <c>{name}</c>, <c>{lang}</c> and <c>{label}</c>; the error
        /// names the missing token(s). Returns <c>(true, null)</c> when valid.
        /// </summary>
        internal static (bool ok, string? error) ValidateTemplate(string? template)
        {
            var missing = new List<string>();

            if (template is null || !template.Contains("{name}", StringComparison.Ordinal))
            {
                missing.Add("{name}");
            }

            if (template is null || !template.Contains("{lang}", StringComparison.Ordinal))
            {
                missing.Add("{lang}");
            }

            if (template is null || !template.Contains("{label}", StringComparison.Ordinal))
            {
                missing.Add("{label}");
            }

            if (missing.Count > 0)
            {
                return (false, "Template is missing required token(s): " + string.Join(", ", missing));
            }

            return (true, null);
        }

        /// <summary>
        /// The template to actually use: a valid <paramref name="template"/> as-is, else
        /// <see cref="DefaultTemplate"/> — so an invalid persisted template still produces valid names.
        /// </summary>
        internal static string EffectiveTemplate(string? template)
        {
            return ValidateTemplate(template).ok ? template! : DefaultTemplate;
        }
    }
}
