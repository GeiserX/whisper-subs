using System;
using WhisperSubs.Controller.Workers;
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

    /// <summary>
    /// Why this server's own Canary install can or cannot serve a target. Only chooses the wording of an
    /// <see cref="TranslationEngine.EngineMissing"/> reason.
    /// </summary>
    public enum LocalCanaryState
    {
        /// <summary>The crispasr binary or the Canary model file is missing.</summary>
        NotInstalled,

        /// <summary>Installed, and the running pool holds this server's worker.</summary>
        InPool,

        /// <summary>
        /// Installed, and the current settings include this server, but the running pool was built before
        /// that change and does not hold it yet.
        /// </summary>
        PendingPoolRebuild,

        /// <summary>Installed, but "Also use this server as a worker" is off with remote rows configured.</summary>
        LocalWorkerOff,

        /// <summary>Installed, but a single legacy Remote API URL makes the pool remote-only.</summary>
        LegacyRemoteOnly,
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
        /// <param name="localCanary">Whether this server has the engine and is a worker in the pool. Only
        /// changes the reason when <paramref name="canaryAvailable"/> is false, so it names the real fix.</param>
        public static TranslationRouteDecision Decide(
            string? sourceLanguage, string? targetLanguage, bool canaryAvailable,
            LocalCanaryState localCanary = LocalCanaryState.NotInstalled)
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

            if (!canaryAvailable)
            {
                return new(TranslationEngine.EngineMissing, localCanary switch
                {
                    LocalCanaryState.LocalWorkerOff =>
                        $"A '{target}' subtitle needs the Canary engine. It is installed on this server, but \"Also use this server as a worker\" is off under Worker Pool and no CrispASR server row lists '{target}'. Turn that option on, or add a CrispASR server row that lists '{target}'.",
                    LocalCanaryState.LegacyRemoteOnly =>
                        $"A '{target}' subtitle needs the Canary engine. It is installed on this server, but while a single Remote API URL is set, only that remote server transcribes. Add it as a row under Worker Pool instead, so this server can work too, or add a CrispASR server row that lists '{target}'.",
                    LocalCanaryState.PendingPoolRebuild =>
                        $"A '{target}' subtitle needs the Canary engine. It is installed on this server, and the worker settings now include this server, but the running pool was built before that change. It picks the change up when it is rebuilt, at the next start of work with nothing transcribing; generate again then.",
                    LocalCanaryState.InPool =>
                        $"A '{target}' subtitle needs the Canary engine. It is installed on this server, which is a worker in the pool, but the pool does not offer '{target}' yet. The pool re-checks the install within a minute; generate again then.",
                    _ =>
                        $"A '{target}' subtitle needs the Canary engine, which is not installed. Download the crispasr binary and the Canary model on the settings page, or add a CrispASR worker that lists '{target}'.",
                });
            }

            return new(TranslationEngine.Canary, $"English audio translated into '{target}' by Canary.");
        }

        /// <summary>
        /// Why no engine can make <paramref name="target"/> at all, whatever the audio: the same wording a
        /// translate job would fail with for English audio, so refusing a request up front and failing the
        /// job read alike. Null when an engine can serve it, and always for "en" (Whisper). Pure.
        /// </summary>
        public static string? EngineMissingReason(string target, bool canaryAvailable, LocalCanaryState localCanary)
        {
            var d = Decide("en", target, canaryAvailable, localCanary);
            return d.Engine is TranslationEngine.EngineMissing or TranslationEngine.Unsupported ? d.Reason : null;
        }

        /// <summary>
        /// What a viewer is told when no engine can make the language they asked for. The reasons above name
        /// server settings, which are the admin's business, so a viewer gets this fixed sentence instead.
        /// </summary>
        public const string ViewerEngineMissing = "That language cannot be made on this server right now.";

        /// <summary>
        /// Why no engine can make an English subtitle, for the 409 and for a translate job that fails: the
        /// local Whisper model cannot translate (<paramref name="localInPool"/> and not
        /// <paramref name="localWhisperTranslates"/>), or no worker in the pool translates into English. Pure.
        /// </summary>
        public static string EnglishMissingReason(bool localInPool, bool localWhisperTranslates, string? modelPath)
            => localInPool && !localWhisperTranslates
                ? $"An English subtitle comes from the Whisper model on this server, and the active model \"{System.IO.Path.GetFileName(modelPath)}\" is a turbo model, which was not trained to translate: it would write the audio's own language under an English name. Activate a model that is not turbo, such as Large V3 (Q5), on the setup page, or add a worker row that lists 'en' under Translation targets."
                : "No worker in the pool translates into English. Turn on \"Also use this server as a worker\" under Worker Pool, or list 'en' under Translation targets on a worker row.";

        /// <summary>
        /// The state that picks the <see cref="TranslationEngine.EngineMissing"/> wording: missing files
        /// first, then whether the running pool holds this server (<paramref name="localWorkerInPool"/>,
        /// its real composition). Only when it does not do the current settings explain why, through
        /// <see cref="WorkerPlan.HostsLocal"/>, the rule the registry builds from: settings that would now
        /// include it mean the pool predates the change. Without a pool (<paramref name="localWorkerInPool"/>
        /// null) the only engine is this server's install as read when the pass began, and it can only have
        /// failed because the engine was not installed then, even if the files have appeared since. Pure.
        /// </summary>
        public static LocalCanaryState LocalCanary(
            bool installed, bool? localWorkerInPool,
            int explicitWorkerCount, int usableExplicitRows, bool hasLegacyRemoteUrl, bool enableLocalWorker)
        {
            if (!installed || localWorkerInPool is null) return LocalCanaryState.NotInstalled;
            if (localWorkerInPool.Value) return LocalCanaryState.InPool;
            if (WorkerPlan.HostsLocal(explicitWorkerCount, usableExplicitRows, hasLegacyRemoteUrl, enableLocalWorker)) return LocalCanaryState.PendingPoolRebuild;
            return WorkerPlan.Decide(explicitWorkerCount, hasLegacyRemoteUrl, enableLocalWorker).Source == WorkerSource.LegacyRemote
                ? LocalCanaryState.LegacyRemoteOnly
                : LocalCanaryState.LocalWorkerOff;
        }
    }
}
