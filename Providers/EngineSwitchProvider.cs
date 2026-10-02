using System;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace WhisperSubs.Providers
{
    /// <summary>
    /// This server's provider when the admin may switch the transcription engine. Whole-title
    /// transcription goes to Qwen3-ASR while it is selected and installed, read live on every job so
    /// a change on the settings page takes effect without rebuilding the pool. Everything Qwen3-ASR
    /// cannot do stays on Whisper: translation into English, language detection, and transcription
    /// whenever Qwen3-ASR is not selected or its files are missing.
    /// </summary>
    public sealed class EngineSwitchProvider : ISubtitleProvider
    {
        private readonly Func<Qwen3Provider?> _qwen3;
        private readonly Microsoft.Extensions.Logging.ILogger? _logger;

        public EngineSwitchProvider(WhisperProvider whisper, Func<Qwen3Provider?> qwen3, Microsoft.Extensions.Logging.ILogger? logger = null)
        {
            Whisper = whisper;
            _qwen3 = qwen3;
            _logger = logger;
        }

        /// <summary>The Whisper provider every non-transcription call lands on.</summary>
        public WhisperProvider Whisper { get; }

        private volatile ISubtitleProvider? _lastUsed;

        /// <summary>The engine a transcription would use right now, before the language is known.</summary>
        public ISubtitleProvider Current => (ISubtitleProvider?)_qwen3() ?? Whisper;

        /// <summary>
        /// The engine the last transcription actually ran on, or <see cref="Current"/> before any. The
        /// manager reads it right after the transcription it belongs to; this server's worker holds one
        /// slot, so no other job runs between the two.
        /// </summary>
        public ISubtitleProvider LastUsed => _lastUsed ?? Current;

        public string Name => Current.Name;

        /// <summary>
        /// Of the engine that ran: Qwen3-ASR (VAD or aligner timing, opt-in) or Whisper (its own VAD
        /// rule), which differ when a title's language sent the job to Whisper.
        /// </summary>
        public bool RequiresSpeechAlignmentOptIn => LastUsed.RequiresSpeechAlignmentOptIn;

        public Task<string> TranscribeAsync(string audioPath, string language, CancellationToken cancellationToken, bool translate = false, string? targetLanguage = null)
            => TranscribeAsync(audioPath, language, cancellationToken, translate, applyVad: true, targetLanguage);

        /// <summary>
        /// The forced-subtitle path passes <paramref name="applyVad"/> false for a chunk that is already
        /// one speech window; both engines honour it.
        /// </summary>
        public async Task<string> TranscribeAsync(string audioPath, string language, CancellationToken cancellationToken, bool translate, bool applyVad, string? targetLanguage = null)
        {
            var qwen3 = IsTranscription(translate, targetLanguage) ? _qwen3() : null;
            if (qwen3 != null)
            {
                // Qwen3-ASR always gets a concrete language: with none, crispasr v0.8.35 fetches a
                // Whisper tiny model for identification instead of asking the model, and falls back
                // to English when that fails. The plugin's own detection model answers instead.
                var code = Qwen3Provider.NormalizeLanguage(language);
                if (code == null)
                {
                    var (detected, _) = await Whisper.DetectLanguageAsync(audioPath, cancellationToken);
                    code = Qwen3Provider.NormalizeLanguage(detected)
                        ?? throw new InvalidOperationException("The language of the audio could not be detected, and Qwen3-ASR needs one.");
                }
                // Only the model's 30 languages go to crispasr; a Swahili or Welsh title stays on
                // Whisper rather than failing or coming back in the wrong language.
                if (Setup.Qwen3Catalog.Supports(code))
                {
                    _lastUsed = qwen3;
                    return await qwen3.TranscribeAsync(audioPath, code, cancellationToken, translate: false, targetLanguage: null, applyVad);
                }
                _logger?.LogInformation("Qwen3-ASR does not cover '{Language}'; transcribing with Whisper instead", code);
                language = code;
            }
            _lastUsed = Whisper;
            // A target is only ever "en" here (Whisper refuses anything else); the interface overload
            // checks it, the applyVad overload is the one the forced path needs.
            return targetLanguage == null
                ? await Whisper.TranscribeAsync(audioPath, language, cancellationToken, translate, applyVad)
                : await Whisper.TranscribeAsync(audioPath, language, cancellationToken, translate, targetLanguage);
        }

        /// <summary>A plain transcription, the only call Qwen3-ASR takes. Pure.</summary>
        internal static bool IsTranscription(bool translate, string? targetLanguage)
            => !translate && string.IsNullOrWhiteSpace(targetLanguage);

        public Task<(string Language, float Probability)> DetectLanguageAsync(string audioPath, CancellationToken cancellationToken)
            => Whisper.DetectLanguageAsync(audioPath, cancellationToken);
    }
}
