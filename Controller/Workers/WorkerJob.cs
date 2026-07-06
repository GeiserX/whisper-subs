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
        /// translation is possible for the config every job requires a translate-capable worker, guaranteeing
        /// a translate pass never lands on a transcribe-only worker. A worker that cannot translate is simply
        /// not chosen while translation is enabled — the common case (all workers translate) is unaffected.
        /// <c>RequiredModel</c> is null: the manager does not pin a per-job model, so any model serves.
        /// </summary>
        public static JobRequirements Requirements(SubtitleMode mode, bool enableTranslation)
            => new JobRequirements(TranslationPossible(mode, enableTranslation), null);
    }
}
