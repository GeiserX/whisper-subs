using WhisperSubs.Providers;

namespace WhisperSubs.Controller.Workers
{
    /// <summary>
    /// A transcription worker (v4.0 pool): a stable identity + advertised <see cref="WorkerCapabilities"/>
    /// + the <see cref="ISubtitleProvider"/> that does the actual work — the host's own local whisper-cli
    /// or any remote OpenAI-compatible endpoint. The dispatcher (PR-3) tracks live in-flight load
    /// separately and snapshots each worker into a <see cref="WorkerSlot"/> for routing, so this stays a
    /// thin, immutable descriptor.
    /// </summary>
    public interface ITranscriptionWorker
    {
        string Id { get; }
        string Name { get; }
        ISubtitleProvider Provider { get; }
        WorkerCapabilities Capabilities { get; }

        /// <summary>
        /// The provider for a Canary translation target, when it is not <see cref="Provider"/>: the host's
        /// own worker transcribes with whisper-cli but translates into Canary targets with crispasr. Null
        /// means <see cref="Provider"/> serves every target in <see cref="WorkerCapabilities.TranslateTargets"/>
        /// (a CrispASR server does).
        /// </summary>
        ISubtitleProvider? TargetProvider => null;
    }

    /// <summary>Default worker: an id/name + the provider that transcribes + its advertised capabilities.</summary>
    public sealed record TranscriptionWorker(
        string Id, string Name, ISubtitleProvider Provider, WorkerCapabilities Capabilities,
        ISubtitleProvider? TargetProvider = null) : ITranscriptionWorker;
}
