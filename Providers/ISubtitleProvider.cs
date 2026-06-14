using System.Threading;
using System.Threading.Tasks;

namespace WhisperSubs.Providers
{
    public interface ISubtitleProvider
    {
        string Name { get; }

        /// <summary>
        /// True when this provider emits subtitles already aligned to speech onset (e.g. via
        /// whisper-cli native VAD), so the FFmpeg silence-detection alignment pass should be
        /// skipped. Single source of truth — avoids re-resolving VAD state elsewhere.
        /// </summary>
        bool UsesVad { get; }

        Task<string> TranscribeAsync(string audioPath, string language, CancellationToken cancellationToken, bool translate = false);

        /// <summary>
        /// Detects the language spoken in an audio file.
        /// Returns the ISO 639-1 language code and confidence probability.
        /// </summary>
        Task<(string Language, float Probability)> DetectLanguageAsync(string audioPath, CancellationToken cancellationToken);
    }
}
