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

        /// <summary>
        /// The audio is not English (or its language is unknown). A Canary target can only be made from
        /// English audio, so the target is skipped with an Information log line: no error, no file. This is
        /// the normal case for most of a library, not a fault.
        /// </summary>
        SkipSourceNotEnglish,

        /// <summary>
        /// English audio and a valid target, but no engine can serve it: the crispasr binary or the Canary
        /// model is not installed and no worker lists the target. The target fails with a clear per-item
        /// error and no file is written.
        /// </summary>
        EngineMissing,

        /// <summary>
        /// The target itself is invalid (empty, or outside <see cref="CanaryCatalog.Targets"/>), which means
        /// the configuration is corrupt. The target fails with the reason and no file is written.
        /// </summary>
        Unsupported,
    }

    /// <summary>The route for one (source, target) pair and a human-readable reason.</summary>
    public readonly record struct TranslationRouteDecision(TranslationEngine Engine, string Reason);

    /// <summary>
    /// Pure decision: which engine can translate audio in <c>sourceLanguage</c> into
    /// <c>targetLanguage</c>? Whisper translates only into English. Canary translates English
    /// audio into 24 European languages and has no direct path between two non-English languages
    /// (a spike got code-mixed or dropped cues with exit code 0), so a non-English or unknown source is
    /// skipped, a missing engine fails the target, and an invalid target is Unsupported. This is the only
    /// guard against that silent failure, so callers must consult it before any process is spawned, and
    /// must never write a file for any route other than Whisper or Canary.
    /// </summary>
    public static class TranslationRoute
    {
        /// <param name="canaryAvailable">Some engine can serve the target: a local install that is in the
        /// pool, or a worker that lists it.</param>
        /// <param name="canaryInstalledHere">crispasr and the Canary model are installed on this server. Only
        /// changes the reason when <paramref name="canaryAvailable"/> is false: the install exists, but this
        /// server is not a worker in the pool, so installing again would not help.</param>
        public static TranslationRouteDecision Decide(
            string? sourceLanguage, string? targetLanguage, bool canaryAvailable, bool canaryInstalledHere = false)
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
                return new(TranslationEngine.SkipSourceNotEnglish,
                    $"The audio language could not be determined, and a '{target}' subtitle can only be made from English audio.");
            }

            if (!string.Equals(source, "en", StringComparison.OrdinalIgnoreCase))
            {
                return new(TranslationEngine.SkipSourceNotEnglish,
                    $"The audio is '{source}'. A '{target}' subtitle can only be made from English audio; other languages translate to English only.");
            }

            if (!canaryAvailable && canaryInstalledHere)
            {
                return new(TranslationEngine.EngineMissing,
                    $"A '{target}' subtitle needs the Canary engine. It is installed on this server, but this server is not a worker in the pool. Turn on \"Also use this server as a worker\" under Worker Pool, or add a CrispASR server row that lists '{target}'. A single Remote API URL never uses this server, so add that server as a worker row instead.");
            }

            if (!canaryAvailable)
            {
                return new(TranslationEngine.EngineMissing,
                    $"A '{target}' subtitle needs the Canary engine, which is not installed. Download the crispasr binary and the Canary model on the settings page, or add a CrispASR worker that lists '{target}'.");
            }

            return new(TranslationEngine.Canary, $"English audio translated into '{target}' by Canary.");
        }
    }
}
