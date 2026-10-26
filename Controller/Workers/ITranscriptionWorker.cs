using System.Threading;
using System.Threading.Tasks;
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

        /// <summary>
        /// Re-reads what the worker's server offers, at the start of a run, so <see cref="Capabilities"/> says what
        /// it can do now. Never throws. Nothing to read for most workers.
        /// </summary>
        Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Default worker: an id/name + the provider that transcribes + its advertised capabilities.</summary>
    public sealed record TranscriptionWorker(
        string Id, string Name, ISubtitleProvider Provider, WorkerCapabilities Capabilities,
        ISubtitleProvider? TargetProvider = null) : ITranscriptionWorker;

    /// <summary>
    /// An akou row: <see cref="TranscriptionWorker"/> plus English translation while its server offers the
    /// translate task (<see cref="RemoteWhisperProvider.AkouTranslates"/>, read by <see cref="RefreshAsync"/>).
    /// Against an older akou its capabilities are <paramref name="baseCapabilities"/>, as in 4.17.
    /// </summary>
    public sealed class AkouTranscriptionWorker(
        string id, string name, ISubtitleProvider provider, WorkerCapabilities baseCapabilities, RemoteWhisperProvider akou) : ITranscriptionWorker
    {
        public string Id => id;

        public string Name => name;

        public ISubtitleProvider Provider => provider;

        public WorkerCapabilities Capabilities => akou.AkouTranslates && !baseCapabilities.TranslateTargets.Contains("en")
            ? baseCapabilities with { TranslateTargets = WorkerTargets.EnglishOnly }
            : baseCapabilities;

        public Task RefreshAsync(CancellationToken cancellationToken) => akou.RefreshAkouServerAsync(cancellationToken);
    }
}
