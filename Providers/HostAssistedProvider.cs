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

        public async Task<string> TranscribeAsync(string audioPath, string language, CancellationToken cancellationToken, bool translate = false, string? targetLanguage = null)
        {
            if (IsTranslation(translate, targetLanguage))
            {
                return await Whisper.TranscribeAsync(audioPath, language, cancellationToken, translate, targetLanguage);
            }
            // Untagged audio arrives as "auto". The server would then run the detection this dialect
            // exists to avoid, so the host's Whisper names the language first; a language the model
            // does not cover stays on the host's Whisper too, as the local engine switch does.
            var code = Qwen3Provider.NormalizeLanguage(language);
            if (code == null)
            {
                var (detected, _) = await Whisper.DetectLanguageAsync(audioPath, cancellationToken);
                code = Qwen3Provider.NormalizeLanguage(detected)
                    ?? throw new System.InvalidOperationException("The language of the audio could not be detected, and the Qwen3-ASR server needs one.");
            }
            return Setup.Qwen3Catalog.Supports(code)
                ? await Remote.TranscribeAsync(audioPath, code, cancellationToken, translate, targetLanguage)
                : await Whisper.TranscribeAsync(audioPath, code, cancellationToken, translate, targetLanguage);
        }

        /// <summary>A translate request, or any target: the host's Whisper takes it. Pure.</summary>
        internal static bool IsTranslation(bool translate, string? targetLanguage)
            => translate || !string.IsNullOrWhiteSpace(targetLanguage);

        public Task<(string Language, float Probability)> DetectLanguageAsync(string audioPath, CancellationToken cancellationToken)
            => Whisper.DetectLanguageAsync(audioPath, cancellationToken);
    }
}
