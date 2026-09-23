using System.Threading;
using System.Threading.Tasks;

namespace WhisperSubs.Providers
{
    public interface ISubtitleProvider
    {
        string Name { get; }

        /// <summary>
        /// True when the provider's timing should not be rewritten unless the admin explicitly
        /// enables the extra speech-alignment pass. Local native VAD and remote/provider-owned
        /// timestamps both require that opt-in.
        /// </summary>
        bool RequiresSpeechAlignmentOptIn { get; }

        /// <summary>
        /// Transcribes <paramref name="audioPath"/>, or translates it when <paramref name="translate"/>
        /// is set. <paramref name="targetLanguage"/> null or "en" is the English translation every
        /// Whisper provider supports; any other target is only honoured by the Canary provider, and
        /// Whisper providers throw <see cref="System.NotSupportedException"/> for it.
        /// </summary>
        Task<string> TranscribeAsync(string audioPath, string language, CancellationToken cancellationToken, bool translate = false, string? targetLanguage = null);

        /// <summary>
        /// Detects the language spoken in an audio file.
        /// Returns the ISO 639-1 language code and confidence probability.
        /// </summary>
        Task<(string Language, float Probability)> DetectLanguageAsync(string audioPath, CancellationToken cancellationToken);
    }
}
