using System;
using WhisperSubs.Setup;

namespace WhisperSubs.Controller
{
    /// <summary>Which engine produces a translated subtitle for one target language.</summary>
    public enum TranslationEngine
    {
        /// <summary>whisper-cli's <c>--translate</c> task. The only engine for an English target.</summary>
        Whisper,

        /// <summary>NVIDIA Canary through crispasr, for a non-English target from English audio.</summary>
        Canary,

        /// <summary>No engine can honour the pair. The item fails with the reason and no file is written.</summary>
        Unsupported,
    }

    /// <summary>The route for one (source, target) pair and a human-readable reason.</summary>
    public readonly record struct TranslationRouteDecision(TranslationEngine Engine, string Reason);

    /// <summary>
    /// Pure decision: which engine can translate audio in <c>sourceLanguage</c> into
    /// <c>targetLanguage</c>? Whisper translates only into English. Canary translates English
    /// audio into 24 European languages and has no direct path between two non-English languages
    /// (a spike got code-mixed or dropped cues with exit code 0), so anything else is Unsupported.
    /// This is the only guard against that silent failure, so callers must consult it before any
    /// process is spawned, and must never write a file for an Unsupported route.
    /// </summary>
    public static class TranslationRoute
    {
        public static TranslationRouteDecision Decide(string? sourceLanguage, string? targetLanguage, bool canaryAvailable)
        {
            var target = (targetLanguage ?? "").Trim();
            var source = (sourceLanguage ?? "").Trim();

            if (target.Length == 0)
            {
                return new(TranslationEngine.Unsupported, "No target language was given.");
            }

            if (string.Equals(target, "en", StringComparison.OrdinalIgnoreCase))
            {
                return new(TranslationEngine.Whisper, "English subtitles come from Whisper's translate task.");
            }

            if (!CanaryCatalog.IsTarget(target))
            {
                return new(TranslationEngine.Unsupported,
                    $"'{target}' is not a language this plugin can translate into. Supported targets are English and the Canary list on the settings page.");
            }

            if (source.Length == 0 || string.Equals(source, "auto", StringComparison.OrdinalIgnoreCase))
            {
                return new(TranslationEngine.Unsupported,
                    $"The audio language could not be determined, and a '{target}' subtitle can only be made from English audio.");
            }

            if (!string.Equals(source, "en", StringComparison.OrdinalIgnoreCase))
            {
                return new(TranslationEngine.Unsupported,
                    $"The audio is '{source}'. A '{target}' subtitle can only be made from English audio; other languages translate to English only.");
            }

            if (!canaryAvailable)
            {
                return new(TranslationEngine.Unsupported,
                    $"A '{target}' subtitle needs the Canary engine, which is not installed. Download the crispasr binary and the Canary model on the settings page.");
            }

            return new(TranslationEngine.Canary, $"English audio translated into '{target}' by Canary.");
        }
    }
}
