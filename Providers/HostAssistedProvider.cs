using System;
using System.Threading;
using System.Threading.Tasks;
using WhisperSubs.Controller.Workers;

namespace WhisperSubs.Providers
{
    /// <summary>
    /// A remote worker that borrows this server's Whisper for what it cannot do. The Qwen3-ASR server
    /// dialect needs both halves: language detection, because crispasr's own detection there is Whisper
    /// tiny on the first seconds (it named Norwegian for English and Spanish test clips) and with it off
    /// the server reports "auto"; and translation into English, which Qwen3-ASR has no task for, yet the
    /// forced-subtitle pass requests for foreign lines inside an English title even with the translation
    /// option off. Plain transcription, the name and the timing rule stay the remote worker's.
    /// <para>
    /// An akou row works without a Whisper here: when this server has none, akou names the language itself
    /// (<see cref="RemoteWhisperProvider.DetectAkouLanguageAsync"/>), and a language Qwen3-ASR does not cover
    /// ends as <see cref="LanguageNotCoveredException"/> instead of a missing-file error.
    /// </para>
    /// </summary>
    public sealed class HostAssistedProvider : ISubtitleProvider
    {
        private readonly Func<bool> _hostWhisperInstalled;

        /// <param name="hostWhisperInstalled">Whether this server's whisper-cli and its model are on disk, read
        /// on each call. Null asks <see cref="WhisperProvider.IsInstalled"/>.</param>
        public HostAssistedProvider(ISubtitleProvider remote, WhisperProvider whisper, Func<bool>? hostWhisperInstalled = null)
        {
            Remote = remote;
            Whisper = whisper;
            _hostWhisperInstalled = hostWhisperInstalled ?? whisper.IsInstalled;
        }

        /// <summary>The worker that transcribes.</summary>
        public ISubtitleProvider Remote { get; }

        /// <summary>This server's Whisper, which detects and translates.</summary>
        public WhisperProvider Whisper { get; }

        public string Name => Remote.Name;

        public bool RequiresSpeechAlignmentOptIn => Remote.RequiresSpeechAlignmentOptIn;

        /// <summary>True while this server's Whisper can run: the detection the forced pass needs, translation, uncovered languages.</summary>
        public bool HostWhisperInstalled => _hostWhisperInstalled();

        /// <summary>The akou row's provider, or null for any other dialect.</summary>
        private RemoteWhisperProvider? Akou => Remote is RemoteWhisperProvider { Dialect: WorkerDialect.Akou } akou ? akou : null;

        public async Task<string> TranscribeAsync(string audioPath, string language, CancellationToken cancellationToken, bool translate = false, string? targetLanguage = null)
        {
            if (IsTranslation(translate, targetLanguage))
            {
                return await Whisper.TranscribeAsync(audioPath, language, cancellationToken, translate, targetLanguage);
            }
            // Untagged audio arrives as "auto". The server would then run the detection this dialect
            // exists to avoid, so the language is named first (this server's Whisper, or akou when there
            // is none); a language the model does not cover stays on the host's Whisper too, as the local
            // engine switch does.
            var code = Qwen3Provider.NormalizeLanguage(language);
            if (code == null)
            {
                var (detected, _) = await DetectLanguageAsync(audioPath, cancellationToken);
                code = Qwen3Provider.NormalizeLanguage(detected)
                    ?? throw new InvalidOperationException("The language of the audio could not be detected, and the Qwen3-ASR server needs one.");
            }
            if (Setup.Qwen3Catalog.Supports(code))
            {
                return await Remote.TranscribeAsync(audioPath, code, cancellationToken, translate, targetLanguage);
            }
            if (!HostWhisperInstalled) throw NotCovered(code);
            return await Whisper.TranscribeAsync(audioPath, code, cancellationToken, translate, targetLanguage);
        }

        /// <summary>
        /// Whether this row can transcribe <paramref name="language"/>: an untagged title (named later), one
        /// of Qwen3-ASR's languages, or any language while this server's Whisper is installed.
        /// </summary>
        public bool Covers(string? language)
        {
            var code = Qwen3Provider.NormalizeLanguage(language);
            return code == null || Setup.Qwen3Catalog.Supports(code) || HostWhisperInstalled;
        }

        /// <summary>The error for a language this row cannot take, naming the engine.</summary>
        public LanguageNotCoveredException NotCovered(string language)
            => new(Qwen3Provider.NormalizeLanguage(language) ?? language, Akou != null ? "akou's Qwen3-ASR" : "The Qwen3-ASR server");

        /// <summary>A translate request, or any target: the host's Whisper takes it. Pure.</summary>
        internal static bool IsTranslation(bool translate, string? targetLanguage)
            => translate || !string.IsNullOrWhiteSpace(targetLanguage);

        /// <summary>
        /// This server's Whisper when it is installed, as before. Without it an akou row asks akou; any other
        /// row still asks Whisper, which reports what is missing.
        /// </summary>
        public Task<(string Language, float Probability)> DetectLanguageAsync(string audioPath, CancellationToken cancellationToken)
            => DetectsThroughAkou(HostWhisperInstalled, Akou != null)
                ? Akou!.DetectAkouLanguageAsync(audioPath, cancellationToken)
                : Whisper.DetectLanguageAsync(audioPath, cancellationToken);

        /// <summary>akou names the language only on an akou row, and only without this server's Whisper. Pure.</summary>
        internal static bool DetectsThroughAkou(bool hostWhisperInstalled, bool akouRow) => akouRow && !hostWhisperInstalled;
    }

    /// <summary>
    /// The title's language is not one Qwen3-ASR covers, or akou could not name it, and this server has no
    /// Whisper to take it. The same answer on every run until the title or the configuration changes, so the
    /// title is skipped, not retried, unless another worker in the pool takes it.
    /// </summary>
    public sealed class LanguageNotCoveredException : InvalidOperationException
    {
        public LanguageNotCoveredException(string language, string engine = "akou's Qwen3-ASR")
            : base($"{engine} does not cover {language}; install the local Whisper engine to transcribe it.")
        {
            Language = language;
        }

        private LanguageNotCoveredException(string message, bool undetected)
            : base(message)
        {
            Language = "";
        }

        /// <summary>akou named no language for the title: <paramref name="message"/> says what to do.</summary>
        public static LanguageNotCoveredException Undetected(string message) => new(message, undetected: true);

        /// <summary>The uncovered language code, empty when no language was named.</summary>
        public string Language { get; }
    }
}
