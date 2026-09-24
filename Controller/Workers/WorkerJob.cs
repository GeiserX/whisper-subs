using WhisperSubs.Configuration;

namespace WhisperSubs.Controller.Workers
{
    /// <summary>
    /// Pure mapping from plugin config to the dispatcher's per-job <see cref="JobRequirements"/> (v4.0).
    /// Kept free of Jellyfin runtime types (only the POCO config) so it is unit-testable, mirroring the
    /// codebase's other pure decision helpers (<see cref="PriorityScheduling"/>, <see cref="WorkerPlan"/>).
    /// </summary>
    public static class WorkerJob
    {
        /// <summary>
        /// Whether the current config may require an English-translation pass for some item — the same
        /// condition the scheduled task uses for its <c>needsTranslation</c> gate: TranslationOnly always,
        /// or EnableTranslation in a mode that runs the full pass (Full / FullAndForced).
        /// </summary>
        public static bool TranslationPossible(SubtitleMode mode, bool enableTranslation)
            => mode == SubtitleMode.TranslationOnly
               || (enableTranslation && (mode == SubtitleMode.Full || mode == SubtitleMode.FullAndForced));

        /// <summary>
        /// The capability a dispatched worker must advertise. Whole media items are dispatched and the
        /// manager decides per item whether to also run a translation pass, so this is conservative: when
        /// translation is possible for the config every job requires a worker that translates into English,
        /// guaranteeing a translate pass never lands on a transcribe-only worker. Canary targets are not part
        /// of the item's requirement (most titles skip them); each one asks the pool for its own worker via
        /// <see cref="ForTarget"/>. Whatever the translation setting, a whole-item job never lands on a worker
        /// whose <see cref="WorkerCapabilities.TranscribesItems"/> is false. A worker that cannot translate is simply
        /// not chosen while translation is enabled — the common case (all workers translate) is unaffected.
        /// <c>RequiredModel</c> is null: the manager does not pin a per-job model, so any model serves.
        /// </summary>
        public static JobRequirements Requirements(SubtitleMode mode, bool enableTranslation)
            => new JobRequirements(TranslationPossible(mode, enableTranslation) ? "en" : null, null);

        /// <summary>
        /// The requirement for one translation target: a worker whose
        /// <see cref="WorkerCapabilities.TranslateTargets"/> contains <paramref name="target"/>. For "en" with
        /// <paramref name="localWhisperTranslates"/> false (the local model is a turbo model, which was not
        /// trained to translate and would write the source language under an English name) this server's own
        /// worker is left out. Only a translate job passes that flag; the nightly run never asks for "en" here.
        /// </summary>
        public static JobRequirements ForTarget(string target, bool localWhisperTranslates = true)
        {
            var code = (target ?? "").Trim().ToLowerInvariant();
            return new JobRequirements(code, null, TargetOnly: true, RemoteOnly: code == "en" && !localWhisperTranslates);
        }

        /// <summary>Any worker at all: no translation target, and a worker that takes no whole items counts.</summary>
        public static readonly JobRequirements AnyWorker = new(null, null, TargetOnly: true);

        /// <summary>
        /// The lease the dispatcher takes before it knows which job comes next. While the pool can run
        /// whole items (<paramref name="poolServesItems"/>) that is <paramref name="itemRequirements"/>, as it
        /// always was. When it cannot, every generate job fails fast, and only translate jobs are left: each
        /// one moves to a worker that lists its target, so any worker will do as its first lease, and the
        /// generation settings behind <paramref name="itemRequirements"/> no longer decide its fate.
        /// </summary>
        public static JobRequirements DispatchLease(JobRequirements itemRequirements, bool poolServesItems)
            => poolServesItems ? itemRequirements : AnyWorker;
    }
}
