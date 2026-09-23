using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WhisperSubs.Setup;

namespace WhisperSubs.Providers
{
    /// <summary>
    /// Translates English audio into one of <see cref="CanaryCatalog.Targets"/> with NVIDIA Canary,
    /// through the plugin-managed crispasr binary. It is not a worker in the pool: the translation
    /// pass calls it directly for routes <see cref="Controller.TranslationRoute"/> sends to Canary.
    /// Only ever English source audio; the route decision is the real guard, and this provider
    /// refuses anything else again before spawning a process.
    /// </summary>
    public class CanaryProvider : ISubtitleProvider
    {
        private readonly ILogger _logger;
        private readonly string _binaryPath;
        private readonly string _modelPath;
        private readonly int _threadCount;
        private readonly string _vadModelPath;
        private readonly VadTuning _vadTuning;
        private readonly int _maxLineLength;
        private readonly string _cacheDirectory;

        public CanaryProvider(
            ILogger logger,
            string binaryPath,
            string modelPath,
            int threadCount,
            string vadModelPath,
            VadTuning? vadTuning,
            int maxLineLength,
            string cacheDirectory)
        {
            _logger = logger;
            _binaryPath = binaryPath;
            _modelPath = modelPath;
            _threadCount = threadCount;
            _vadModelPath = vadModelPath;
            _vadTuning = vadTuning ?? VadTuning.Unset;
            _maxLineLength = maxLineLength;
            _cacheDirectory = cacheDirectory;
        }

        public string Name => "Canary";

        /// <summary>Cue timing comes from Silero VAD inside crispasr, so re-alignment is opt-in.</summary>
        public bool RequiresSpeechAlignmentOptIn => true;

        public async Task<string> TranscribeAsync(string audioPath, string language, CancellationToken cancellationToken, bool translate = false, string? targetLanguage = null)
        {
            var target = ValidateRequest(language, targetLanguage);

            if (!File.Exists(_binaryPath)) throw new FileNotFoundException($"crispasr binary not found at: {_binaryPath}");
            if (!File.Exists(_modelPath)) throw new FileNotFoundException($"Canary model not found at: {_modelPath}");
            if (string.IsNullOrEmpty(_vadModelPath) || !File.Exists(_vadModelPath))
            {
                // Without VAD, Canary returns the whole file as one cue, which is not a subtitle.
                throw new InvalidOperationException(
                    "Canary needs the Silero VAD model to split speech into cues, and it is not installed. " +
                    "Turn on \"Use speech detection (VAD)\" once so the model downloads, then retry.");
            }
            if (!File.Exists(audioPath)) throw new FileNotFoundException($"Audio file not found: {audioPath}");

            return await RunAsync(audioPath, target, cancellationToken);
        }

        /// <summary>
        /// Returns the normalized target, or throws when the request is not English audio into a
        /// Canary target. Pure so the refusal is unit-testable.
        /// </summary>
        internal static string ValidateRequest(string? sourceLanguage, string? targetLanguage)
        {
            var target = (targetLanguage ?? "").Trim().ToLowerInvariant();
            if (!CanaryCatalog.IsTarget(target))
            {
                throw new NotSupportedException($"Canary cannot translate into '{targetLanguage}'.");
            }
            if (!string.Equals((sourceLanguage ?? "").Trim(), "en", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException(
                    $"Canary only translates English audio here; the audio language is '{sourceLanguage}'.");
            }
            return target;
        }

        [ExcludeFromCodeCoverage(Justification = "Spawns crispasr")]
        private async Task<string> RunAsync(string audioPath, string target, CancellationToken cancellationToken)
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
            foreach (var arg in BuildArguments(_modelPath, audioPath, target, _threadCount, _vadModelPath, _vadTuning, _maxLineLength, _cacheDirectory, tempOutputPrefix))
            {
                startInfo.ArgumentList.Add(arg);
            }

            _logger.LogInformation("Starting Canary translation into {Target} for {AudioPath}", target, audioPath);

            // Never a WhisperLaunchException: the dispatcher parks the local whisper worker on one,
            // and a broken crispasr must not stop ordinary transcription.
            return await SrtProcessRunner.RunAsync(
                _logger,
                startInfo,
                "Canary",
                tempOutputPrefix + ".srt",
                alternateSrtPath: null,
                (exitCode, stderr) => new InvalidOperationException(DescribeExitFailure(exitCode, stderr)),
                DescribeMissingOutput,
                cancellationToken);
        }

        /// <summary>
        /// The crispasr command line for one Canary translation. Every flag after the target is there
        /// because the spike showed what goes wrong without it:
        /// <list type="bullet">
        /// <item><c>-l en</c> with <c>-sl en</c>: with <c>-l</c> on auto, crispasr downloads Whisper
        /// tiny for language ID even when the source language is given.</item>
        /// <item><c>--no-auto-aligner</c>: the CTC aligner doubles the runtime and keeps using the GPU
        /// under <c>--no-gpu</c>; segment timestamps are fine for subtitles.</item>
        /// <item><c>--cache-dir</c>: keeps anything the binary fetches on its own inside the managed tree.</item>
        /// <item><c>--vad</c>: without it Canary returns the whole file as one cue. The Whisper VAD
        /// tuning flags apply unchanged.</item>
        /// <item><c>--max-len N --split-on-word</c>: the same line-length knob as whisper-cli.</item>
        /// <item>No <c>--prompt</c>: Canary ignores it.</item>
        /// </list>
        /// Pure so the exact vector is unit-testable.
        /// </summary>
        internal static IReadOnlyList<string> BuildArguments(
            string modelPath,
            string audioPath,
            string targetLanguage,
            int threadCount,
            string vadModelPath,
            VadTuning? tuning,
            int maxLineLength,
            string cacheDirectory,
            string outputPrefix)
        {
            var args = new List<string>
            {
                "--backend", "canary",
                "-m", modelPath,
                "-f", audioPath,
                "-l", "en",
                "-sl", "en",
                "-tl", targetLanguage,
            };
            if (threadCount > 0)
            {
                args.Add("-t");
                args.Add(threadCount.ToString(CultureInfo.InvariantCulture));
            }
            args.Add("--no-auto-aligner");
            args.Add("--cache-dir");
            args.Add(cacheDirectory);
            args.Add("--vad");
            args.Add("--vad-model");
            args.Add(vadModelPath);
            WhisperProvider.AppendVadTuning(args, tuning ?? VadTuning.Unset);
            if (maxLineLength > 0)
            {
                args.Add("--max-len");
                args.Add(maxLineLength.ToString(CultureInfo.InvariantCulture));
                args.Add("--split-on-word");
            }
            args.Add("--print-progress");
            args.Add("-osrt");
            args.Add("-of");
            args.Add(outputPrefix);
            return args;
        }

        /// <summary>Canary has no language identification; detection stays on the Whisper base model.</summary>
        public Task<(string Language, float Probability)> DetectLanguageAsync(string audioPath, CancellationToken cancellationToken)
            => throw new NotSupportedException(
                "Canary has no language detection of its own. Language detection runs on the Whisper model.");

        /// <summary>Maps a non-zero crispasr exit to a user-facing message. Pure.</summary>
        internal static string DescribeExitFailure(int exitCode, string? stderr)
        {
            var text = (stderr ?? "").Trim();
            if (exitCode == 127)
            {
                var match = Regex.Match(text, @"error while loading shared libraries:\s*(\S+?):");
                var lib = match.Success ? match.Groups[1].Value : "a shared library";
                return $"crispasr could not start: missing {lib}. Download the crispasr binary again with the CPU variant, " +
                       $"or install the library in the container. Raw error: {Tail(text)}";
            }
            if (exitCode == 132 || exitCode == 134 || exitCode == 135)
            {
                return $"crispasr crashed (exit {exitCode}). This CPU or GPU driver likely does not support the installed build; " +
                       $"download the binary again and pick \"CPU (Compatibility)\" or \"CPU Only\". Raw error: {Tail(text)}";
            }
            return $"crispasr failed with exit code {exitCode}. Error: {Tail(text)}";
        }

        /// <summary>
        /// crispasr exited 0 without writing an SRT: surface its own "error:" line when it printed one.
        /// Pure.
        /// </summary>
        internal static string? DescribeMissingOutput(string? stderr)
        {
            var reported = Regex.Match(stderr ?? "", @"^\s*(?:crispasr:\s*)?error:.*$", RegexOptions.Multiline);
            return reported.Success
                ? "crispasr exited without producing a subtitle file. It reported: " + reported.Value.Trim()
                : null;
        }

        private static string Tail(string text) => text.Length > 1000 ? text[^1000..] : text;
    }
}
