using System.Threading;
using System.Threading.Tasks;

namespace WhisperSubs.Providers
{
    /// <summary>
    /// A remote worker whose language detection runs on this server's Whisper instead. The Qwen3-ASR
    /// server dialect needs it: crispasr's own detection there is Whisper tiny on the first seconds, which
    /// named Norwegian for both English and Spanish test clips, and with it off the server reports
    /// "auto". Transcription, name and timing rule stay the remote worker's.
    /// </summary>
    public sealed class LocalDetectionProvider : ISubtitleProvider
    {
        public LocalDetectionProvider(ISubtitleProvider remote, WhisperProvider whisper)
        {
            Remote = remote;
            Whisper = whisper;
        }

        /// <summary>The worker that transcribes.</summary>
        public ISubtitleProvider Remote { get; }

        /// <summary>This server's Whisper, which detects.</summary>
        public WhisperProvider Whisper { get; }

        public string Name => Remote.Name;

        public bool RequiresSpeechAlignmentOptIn => Remote.RequiresSpeechAlignmentOptIn;

        public Task<string> TranscribeAsync(string audioPath, string language, CancellationToken cancellationToken, bool translate = false, string? targetLanguage = null)
            => Remote.TranscribeAsync(audioPath, language, cancellationToken, translate, targetLanguage);

        public Task<(string Language, float Probability)> DetectLanguageAsync(string audioPath, CancellationToken cancellationToken)
            => Whisper.DetectLanguageAsync(audioPath, cancellationToken);
    }
}
