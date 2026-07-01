namespace WhisperSubs.Providers
{
    /// <summary>
    /// User-tunable whisper-cli native-VAD parameters (issue #105). Each field uses a negative
    /// sentinel to mean "unset — let whisper.cpp use its built-in default", so <see cref="Unset"/>
    /// emits no <c>--vad-*</c> tuning flags at all and the default configuration produces the exact
    /// same command line as before the feature existed. Pure immutable type (no config dependency) so
    /// the argument-building logic that consumes it stays unit-testable.
    /// </summary>
    public sealed record VadTuning(
        float Threshold = -1f,
        int MinSpeechMs = -1,
        int MinSilenceMs = -1,
        float MaxSpeechS = -1f,
        int SpeechPadMs = -1,
        float SamplesOverlap = -1f)
    {
        /// <summary>All-unset tuning: consumers emit no VAD tuning flags.</summary>
        public static readonly VadTuning Unset = new();
    }
}
