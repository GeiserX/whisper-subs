using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace WhisperSubs.Providers
{
    /// <summary>
    /// Transcribes with Qwen3-ASR-1.7B through the plugin-managed crispasr binary. It only ever
    /// transcribes: English translation and language detection stay on Whisper, which is what
    /// <see cref="EngineSwitchProvider"/> arranges. Cue timing comes from Silero VAD inside crispasr,
    /// the same model and tuning the local whisper-cli path uses.
    /// </summary>
    public class Qwen3Provider : ISubtitleProvider
    {
        private readonly ILogger _logger;
        private readonly string _binaryPath;
        private readonly string _modelPath;
        private readonly int _threadCount;
        private readonly string _vadModelPath;
        private readonly VadTuning _vadTuning;
        private readonly int _maxLineLength;
        private readonly string _cacheDirectory;
        private readonly string? _alignerModelPath;

        /// <summary>
        /// Characters per cue when <c>SubtitleMaxLineLength</c> is unset (0). Without a cap a VAD
        /// segment becomes one cue, which the spike measured at over ten seconds.
        /// </summary>
        internal const int DefaultMaxLineLength = 42;

        /// <summary>
        /// Languages written in a two-byte UTF-8 script (Cyrillic, Greek, Hebrew, Arabic, Persian,
        /// Urdu). crispasr counts <c>--max-len</c> in bytes, so their default is doubled to hold the
        /// same number of letters.
        /// </summary>
        private static readonly HashSet<string> TwoByteScriptLanguages = new(StringComparer.Ordinal)
            { "ru", "uk", "bg", "be", "sr", "mk", "kk", "el", "he", "ar", "fa", "ur" };

        /// <summary>Languages written in a three-byte UTF-8 script (CJK, Thai, Indic).</summary>
        private static readonly HashSet<string> ThreeByteScriptLanguages = new(StringComparer.Ordinal)
            { "zh", "ja", "ko", "yue", "th", "hi", "bn", "ta", "te", "ml", "kn", "mr", "gu", "my", "km", "lo", "ne", "si" };

        public Qwen3Provider(
            ILogger logger,
            string binaryPath,
            string modelPath,
            int threadCount,
            string vadModelPath,
            VadTuning? vadTuning,
            int maxLineLength,
            string cacheDirectory,
            string? alignerModelPath = null)
        {
            _logger = logger;
            _binaryPath = binaryPath;
            _modelPath = modelPath;
            _threadCount = threadCount;
            _vadModelPath = vadModelPath;
            _vadTuning = vadTuning ?? VadTuning.Unset;
            _maxLineLength = maxLineLength;
            _cacheDirectory = cacheDirectory;
            _alignerModelPath = string.IsNullOrWhiteSpace(alignerModelPath) ? null : alignerModelPath;
        }

        public string Name => "Qwen3-ASR";

        /// <summary>Cue timing comes from Silero VAD inside crispasr, so re-alignment is opt-in.</summary>
        public bool RequiresSpeechAlignmentOptIn => true;

        public Task<string> TranscribeAsync(string audioPath, string language, CancellationToken cancellationToken, bool translate = false, string? targetLanguage = null)
            => TranscribeAsync(audioPath, language, cancellationToken, translate, targetLanguage, applyVad: true);

        /// <summary>
        /// Transcribes <paramref name="audioPath"/>. <paramref name="applyVad"/> false skips crispasr's
        /// VAD pass for a chunk that is already one speech window (the forced-subtitle path), where
        /// re-running VAD can filter a short window to nothing.
        /// </summary>
        public async Task<string> TranscribeAsync(string audioPath, string language, CancellationToken cancellationToken, bool translate, string? targetLanguage, bool applyVad)
        {
            EnsureTranscribeOnly(translate, targetLanguage);

            if (!File.Exists(_binaryPath)) throw new FileNotFoundException($"crispasr binary not found at: {_binaryPath}");
            if (!File.Exists(_modelPath)) throw new FileNotFoundException($"Qwen3-ASR model not found at: {_modelPath}");
            if (applyVad && (string.IsNullOrEmpty(_vadModelPath) || !File.Exists(_vadModelPath)))
            {
                // Without VAD the whole file is one cue, which is not a subtitle.
                throw new InvalidOperationException(
                    "Qwen3-ASR needs the Silero VAD model to split speech into cues, and it is not installed. " +
                    "Turn on \"Use speech detection (VAD)\" once so the model downloads, then retry.");
            }
            if (!File.Exists(audioPath)) throw new FileNotFoundException($"Audio file not found: {audioPath}");

            return await RunAsync(audioPath, language, applyVad, cancellationToken);
        }

        /// <summary>
        /// Qwen3-ASR has no translate task. A translate request reaching it is a routing bug, so it
        /// throws instead of writing source-language text into an English-named file. Pure.
        /// </summary>
        internal static void EnsureTranscribeOnly(bool translate, string? targetLanguage)
        {
            if (translate || !string.IsNullOrWhiteSpace(targetLanguage))
            {
                throw new NotSupportedException("Qwen3-ASR only transcribes. Translation runs on Whisper or Canary.");
            }
        }

        [ExcludeFromCodeCoverage(Justification = "Spawns crispasr")]
        private async Task<string> RunAsync(string audioPath, string language, bool applyVad, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(_cacheDirectory);
            var tempOutputPrefix = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

            var startInfo = new ProcessStartInfo
            {
                FileName = _binaryPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(_binaryPath) ?? ""
            };
            var aligner = AlignerFor(language, _alignerModelPath, File.Exists);
            foreach (var arg in BuildArguments(_modelPath, audioPath, language, _threadCount, applyVad ? _vadModelPath : null, _vadTuning, _maxLineLength, _cacheDirectory, tempOutputPrefix, aligner))
            {
                startInfo.ArgumentList.Add(arg);
            }

            _logger.LogInformation("Starting Qwen3-ASR transcription [{Language}] for {AudioPath} ({Timing})",
                language, audioPath, aligner != null ? "word aligner" : "VAD timing");

            // Never a WhisperLaunchException: the dispatcher parks the local worker on one, and a broken
            // crispasr must not stop the Whisper passes this worker still serves.
            try
            {
                var srt = await SrtProcessRunner.RunAsync(
                    _logger,
                    startInfo,
                    "Qwen3-ASR",
                    tempOutputPrefix + ".srt",
                    alternateSrtPath: null,
                    (exitCode, stderr) => new InvalidOperationException(CanaryProvider.DescribeExitFailure(exitCode, stderr)),
                    DescribeMissingOutput,
                    cancellationToken);
                return TrimCueLines(srt);
            }
            catch (InvalidOperationException ex) when (ex.Message == NoSpeechMarker)
            {
                // A window of music or silence: crispasr exits 0, warns and writes no file.
                _logger.LogInformation("Qwen3-ASR found no speech in {AudioPath}", audioPath);
                return string.Empty;
            }
        }

        /// <summary>Message that marks "crispasr found no speech" on its way through the shared runner.</summary>
        internal const string NoSpeechMarker = "crispasr found no speech in this audio";

        /// <summary>
        /// crispasr exited 0 without an SRT. "no speech detected" on stderr is a normal answer for a
        /// stretch of music or silence, so it gets its own marker; anything else is described as for
        /// Canary. Pure.
        /// </summary>
        internal static string? DescribeMissingOutput(string? stderr)
            => (stderr ?? "").Contains("no speech detected", StringComparison.OrdinalIgnoreCase)
                ? NoSpeechMarker
                : CanaryProvider.DescribeMissingOutput(stderr);

        /// <summary>
        /// With the aligner, crispasr writes a continuation cue's text with the leading space of the
        /// word boundary it was cut at (" here again, I'm calling the cops."). Trim every line; the
        /// index and timing lines have no spaces to lose. Pure.
        /// </summary>
        internal static string TrimCueLines(string srt)
        {
            if (string.IsNullOrEmpty(srt)) return srt;
            var lines = srt.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                lines[i] = lines[i].TrimEnd('\r').Trim();
            }
            return string.Join("\n", lines);
        }

        /// <summary>
        /// The crispasr command line for one Qwen3-ASR transcription. The flags mirror the Canary
        /// vector where the same reasons apply:
        /// <list type="bullet">
        /// <item><c>--backend qwen3</c>: the 1.7B weights are recognised from the file; there is no
        /// separate backend name to pass for them.</item>
        /// <item><c>-l &lt;code&gt;</c> only for a known language. Resolve <c>auto</c> or empty before
        /// calling this provider (<see cref="EngineSwitchProvider"/> does): without <c>-l</c>, crispasr
        /// v0.8.35 fetches Whisper tiny for identification and defaults to English when that fails,
        /// instead of asking the model, which could identify the language itself.</item>
        /// <item><c>--cache-dir</c>: keeps anything the binary fetches on its own inside the managed tree.</item>
        /// <item><c>--vad</c> with the plugin's Silero model: without it the file is one cue. The
        /// Whisper VAD tuning flags apply unchanged. Omitted when <paramref name="vadModelPath"/> is
        /// null (an already-segmented forced chunk).</item>
        /// <item><c>--max-len N --split-on-word</c>: unset means <see cref="DefaultMaxLineLength"/>,
        /// scaled by the script's UTF-8 width (<see cref="MaxLineLengthFor"/>), never unlimited.</item>
        /// <item><c>-am &lt;aligner&gt; --force-aligner --split-on-punct</c> when
        /// <paramref name="alignerModelPath"/> is given: word timestamps from the Canary CTC aligner
        /// instead of VAD-proportional timing, and cues cut at sentence ends so one does not bridge
        /// the pause after a sentence. <see cref="AlignerFor"/> decides, per language.</item>
        /// </list>
        /// Pure so the exact vector is unit-testable.
        /// </summary>
        internal static IReadOnlyList<string> BuildArguments(
            string modelPath,
            string audioPath,
            string language,
            int threadCount,
            string? vadModelPath,
            VadTuning? tuning,
            int maxLineLength,
            string cacheDirectory,
            string outputPrefix,
            string? alignerModelPath = null)
        {
            var args = new List<string>
            {
                "--backend", "qwen3",
                "-m", modelPath,
                "-f", audioPath,
            };
            var lang = NormalizeLanguage(language);
            if (lang != null)
            {
                args.Add("-l");
                args.Add(lang);
            }
            if (threadCount > 0)
            {
                args.Add("-t");
                args.Add(threadCount.ToString(CultureInfo.InvariantCulture));
            }
            args.Add("--cache-dir");
            args.Add(cacheDirectory);
            if (!string.IsNullOrEmpty(vadModelPath))
            {
                args.Add("--vad");
                args.Add("--vad-model");
                args.Add(vadModelPath);
                WhisperProvider.AppendVadTuning(args, tuning ?? VadTuning.Unset);
            }
            if (!string.IsNullOrEmpty(alignerModelPath))
            {
                args.Add("-am");
                args.Add(alignerModelPath);
                args.Add("--force-aligner");
                // With word timing, a cue cut at sentence punctuation stops bridging the pause after
                // it ("Wait, please." no longer spans the ten seconds before the next line).
                args.Add("--split-on-punct");
            }
            args.Add("--max-len");
            args.Add(MaxLineLengthFor(lang, maxLineLength).ToString(CultureInfo.InvariantCulture));
            args.Add("--split-on-word");
            args.Add("--print-progress");
            args.Add("-osrt");
            args.Add("-of");
            args.Add(outputPrefix);
            return args;
        }

        /// <summary>
        /// The aligner path to pass, or null: the file must be configured and present, and the
        /// language one the Canary aligner knows (<see cref="Setup.Qwen3Catalog.AlignerSupports"/>).
        /// Other languages keep VAD timing rather than failing. Pure.
        /// </summary>
        internal static string? AlignerFor(string? language, string? alignerModelPath, Func<string, bool> fileExists)
            => !string.IsNullOrWhiteSpace(alignerModelPath)
               && Setup.Qwen3Catalog.AlignerSupports(NormalizeLanguage(language))
               && fileExists(alignerModelPath)
                ? alignerModelPath
                : null;

        /// <summary>
        /// The language code to pass, or null for auto-detection (empty, whitespace or <c>auto</c>). Pure.
        /// </summary>
        internal static string? NormalizeLanguage(string? language)
        {
            var code = (language ?? "").Trim().ToLowerInvariant();
            return code.Length == 0 || code == "auto" ? null : code;
        }

        /// <summary>
        /// The <c>--max-len</c> for one language: an explicit setting as is, otherwise
        /// <see cref="DefaultMaxLineLength"/> times the script's UTF-8 bytes per letter, because
        /// crispasr counts bytes. Null (auto-detected language) keeps the single-byte default. Pure.
        /// </summary>
        internal static int MaxLineLengthFor(string? language, int configured)
        {
            if (configured > 0) return configured;
            var code = language ?? "";
            if (ThreeByteScriptLanguages.Contains(code)) return DefaultMaxLineLength * 3;
            if (TwoByteScriptLanguages.Contains(code)) return DefaultMaxLineLength * 2;
            return DefaultMaxLineLength;
        }

        /// <summary>Detection stays on the Whisper model; <see cref="EngineSwitchProvider"/> never routes it here.</summary>
        public Task<(string Language, float Probability)> DetectLanguageAsync(string audioPath, CancellationToken cancellationToken)
            => throw new NotSupportedException(
                "Qwen3-ASR language detection is not used here. Language detection runs on the Whisper model.");
    }
}
