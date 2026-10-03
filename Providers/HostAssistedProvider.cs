using System.Threading;
using System.Threading.Tasks;

namespace WhisperSubs.Providers
{
    /// <summary>
    /// A remote worker that borrows this server's Whisper for what it cannot do. The Qwen3-ASR server
    /// dialect needs both halves: language detection, because crispasr's own detection there is Whisper
    /// tiny on the first seconds (it named Norwegian for English and Spanish test clips) and with it off
    /// the server reports "auto"; and translation into English, which Qwen3-ASR has no task for, yet the
    /// forced-subtitle pass requests for foreign lines inside an English title even with the translation
    /// option off. Plain transcription, the name and the timing rule stay the remote worker's.
    /// </summary>
    public sealed class HostAssistedProvider : ISubtitleProvider
    {
        public HostAssistedProvider(ISubtitleProvider remote, WhisperProvider whisper)
        {
            Remote = remote;
            Whisper = whisper;
        }

        /// <summary>The worker that transcribes.</summary>
        public ISubtitleProvider Remote { get; }

        /// <summary>This server's Whisper, which detects and translates.</summary>
        public WhisperProvider Whisper { get; }

        public string Name => Remote.Name;

        public bool RequiresSpeechAlignmentOptIn => Remote.RequiresSpeechAlignmentOptIn;

        public Task<string> TranscribeAsync(string audioPath, string language, CancellationToken cancellationToken, bool translate = false, string? targetLanguage = null)
            => IsTranslation(translate, targetLanguage)
                ? Whisper.TranscribeAsync(audioPath, language, cancellationToken, translate, targetLanguage)
                : Remote.TranscribeAsync(audioPath, language, cancellationToken, translate, targetLanguage);

        /// <summary>A translate request, or any target: the host's Whisper takes it. Pure.</summary>
        internal static bool IsTranslation(bool translate, string? targetLanguage)
            => translate || !string.IsNullOrWhiteSpace(targetLanguage);

        public Task<(string Language, float Probability)> DetectLanguageAsync(string audioPath, CancellationToken cancellationToken)
            => Whisper.DetectLanguageAsync(audioPath, cancellationToken);
    }
}
