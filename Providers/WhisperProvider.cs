using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WhisperSubs.Controller;
using Microsoft.Extensions.Logging;

namespace WhisperSubs.Providers
{
    public class WhisperProvider : ISubtitleProvider
    {
        private readonly ILogger<WhisperProvider> _logger;
        private readonly string _modelPath;
        private readonly string _binaryPath;
        private readonly int _threadCount;
        private readonly string _customArgs;
        private readonly string _vadModelPath;
        private string? _resolvedExecutable;

        private static readonly HashSet<string> DeniedArgs = new(StringComparer.OrdinalIgnoreCase)
        {
            "-m", "--model", "-f", "--file", "-l", "--language",
            "-t", "--threads", "-mc", "--max-context",
            "-sns", "--suppress-nst",
            "-osrt", "--output-srt", "-ovtt", "--output-vtt",
            "-otxt", "--output-txt", "-olrc", "--output-lrc",
            "-ocsv", "--output-csv", "-oj", "--output-json",
            "-ojf", "--output-json-full", "-owts", "--output-words",
            "-of", "--output-file", "--translate", "-tr",
            "--detect-language", "-dl", "--no-timestamps", "-nt",
            "--prompt", "--offset-n", "-on", "--offset-t", "-ot",
            "--duration", "-d",
            // VAD is plugin-managed (model path + flag + tuning injected below); block manual override.
            "--vad", "-v", "--vad-model", "-vm",
            "--vad-threshold", "-vt", "--vad-min-speech-duration-ms", "-vspd",
            "--vad-min-silence-duration-ms", "-vsd", "--vad-max-speech-duration-s", "-vmsd",
            "--vad-speech-pad-ms", "-vp", "--vad-samples-overlap", "-vo"
        };

        public string Name => "Whisper";

        /// <summary>
        /// True when a usable Silero VAD model is present, meaning whisper-cli will be invoked with
        /// --vad and the emitted subtitles are already speech-aligned. Checked live so it reflects
        /// a model that finished downloading after construction.
        /// </summary>
        public bool UsesVad => !string.IsNullOrEmpty(_vadModelPath) && File.Exists(_vadModelPath);

        public WhisperProvider(ILogger<WhisperProvider> logger, string modelPath, string binaryPath = "", int threadCount = 0, string customArgs = "", string vadModelPath = "")
        {
            _logger = logger;
            _modelPath = modelPath;
            _binaryPath = binaryPath;
            _threadCount = threadCount;
            _customArgs = customArgs ?? "";
            _vadModelPath = vadModelPath ?? "";
        }

        public async Task<string> TranscribeAsync(string audioPath, string language, CancellationToken cancellationToken, bool translate = false)
        {
            _logger.LogInformation("Starting Whisper transcription for {AudioPath} with model {ModelPath}", audioPath, _modelPath);

            if (string.IsNullOrEmpty(_modelPath) || !File.Exists(_modelPath))
            {
                throw new FileNotFoundException($"Whisper model not found at: {_modelPath}");
            }

            if (!File.Exists(audioPath))
            {
                throw new FileNotFoundException($"Audio file not found: {audioPath}");
            }

            return await TranscribeInternalAsync(audioPath, language, cancellationToken, translate);
        }

        [ExcludeFromCodeCoverage(Justification = "Spawns whisper-cli process")]
        private async Task<string> TranscribeInternalAsync(string audioPath, string language, CancellationToken cancellationToken, bool translate)
        {
            var tempOutputPrefix = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            var tempSrtPath = tempOutputPrefix + ".srt";

            try
            {
                var whisperExecutable = FindWhisperExecutable();
                if (whisperExecutable == null)
                {
                    throw new InvalidOperationException(
                        "Whisper executable not found. Please install whisper.cpp and ensure 'whisper-cli' is in PATH, or set the binary path in plugin settings.");
                }

                var langPrompt = GetLanguagePrompt(language);

                var startInfo = new ProcessStartInfo
                {
                    FileName = whisperExecutable,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(whisperExecutable) ?? ""
                };

                // Caller resolves whether VAD applies (model configured + present on disk); the pure
                // arg-builder stays I/O-free and unit-testable.
                var useVad = !string.IsNullOrEmpty(_vadModelPath) && File.Exists(_vadModelPath);
                foreach (var arg in BuildTranscribeArguments(
                    _modelPath, audioPath, language, _threadCount, translate,
                    useVad ? _vadModelPath : null, tempOutputPrefix, langPrompt))
                {
                    startInfo.ArgumentList.Add(arg);
                }

                AppendCustomArgs(startInfo);

                _logger.LogInformation("Running: {Executable} {Arguments} (cwd: {WorkingDirectory})",
                    whisperExecutable, string.Join(" ", startInfo.ArgumentList), startInfo.WorkingDirectory);

                using var process = new Process { StartInfo = startInfo };

                var errorBuilder = new StringBuilder();

                process.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        _logger.LogDebug("Whisper output: {Output}", e.Data);
                    }
                };

                process.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        errorBuilder.AppendLine(e.Data);

                        // Progress lines drive the per-file bar; everything else is a real warning.
                        if (TryParseProgress(e.Data, out var pct))
                        {
                            SubtitleQueueService.Instance.ReportFileProgress(pct);
                        }
                        else
                        {
                            _logger.LogWarning("Whisper stderr: {Error}", e.Data);
                        }
                    }
                };

                // Reset the per-file progress bar to 0 at the start of every whisper run. This is the
                // single choke point all transcription paths funnel through (full / translation /
                // forced-segment / lyrics / resume) and is reached by both the scheduled task and the
                // manual Generate drain loop — so the manual path (which has no item-level reset)
                // no longer shows the previous run's stale 100% and runs backwards. The scheduled
                // task also resets at item start (before audio extraction); that reset stays.
                SubtitleQueueService.Instance.ResetFileProgress();

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                try
                {
                    await process.WaitForExitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }

                    // Save whatever partial SRT whisper wrote before being killed
                    if (File.Exists(tempSrtPath))
                    {
                        var partial = await File.ReadAllTextAsync(tempSrtPath);
                        if (!string.IsNullOrWhiteSpace(partial))
                        {
                            _logger.LogInformation("Cancelled — returning partial SRT ({Bytes} bytes)", partial.Length);
                            return partial;
                        }
                    }

                    throw;
                }

                // Flush async stdout/stderr pipe buffers so errorBuilder is complete
                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    var stderr = errorBuilder.ToString();
                    var failure = DescribeWhisperExitFailure(process.ExitCode, stderr);
                    if (failure != null) throw new InvalidOperationException(failure);
                    throw new InvalidOperationException($"Whisper process failed with exit code {process.ExitCode}. Error: {stderr}");
                }

                if (File.Exists(tempSrtPath))
                {
                    var srtContent = await File.ReadAllTextAsync(tempSrtPath, cancellationToken);
                    _logger.LogInformation("Successfully generated subtitle file");
                    return srtContent;
                }

                var altPath = Path.ChangeExtension(audioPath, ".srt");
                if (File.Exists(altPath))
                {
                    var srtContent = await File.ReadAllTextAsync(altPath, cancellationToken);
                    return srtContent;
                }

                throw new FileNotFoundException($"Subtitle file not found at expected location: {tempSrtPath}");
            }
            finally
            {
                if (File.Exists(tempSrtPath))
                {
                    try { File.Delete(tempSrtPath); }
                    catch { }
                }
            }
        }

        public async Task<(string Language, float Probability)> DetectLanguageAsync(string audioPath, CancellationToken cancellationToken)
        {
            _logger.LogInformation("Detecting language for {AudioPath}", audioPath);

            if (string.IsNullOrEmpty(_modelPath) || !File.Exists(_modelPath))
            {
                throw new FileNotFoundException($"Whisper model not found at: {_modelPath}");
            }

            if (!File.Exists(audioPath))
            {
                throw new FileNotFoundException($"Audio file not found: {audioPath}");
            }

            return await DetectLanguageInternalAsync(audioPath, cancellationToken);
        }

        [ExcludeFromCodeCoverage(Justification = "Spawns whisper-cli process for language detection")]
        private async Task<(string Language, float Probability)> DetectLanguageInternalAsync(string audioPath, CancellationToken cancellationToken)
        {
            var whisperExecutable = FindWhisperExecutable();
            if (whisperExecutable == null)
            {
                throw new InvalidOperationException(
                    "Whisper executable not found. Please install whisper.cpp and ensure 'whisper-cli' is in PATH, or set the binary path in plugin settings.");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = whisperExecutable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(whisperExecutable) ?? ""
            };

            startInfo.ArgumentList.Add("-m");
            startInfo.ArgumentList.Add(_modelPath);
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add(audioPath);
            startInfo.ArgumentList.Add("-l");
            startInfo.ArgumentList.Add("auto");
            // Cap detection at 4 threads — detection is a trivial workload that doesn't
            // benefit from high parallelism, and using 16 threads causes unnecessary CPU spikes.
            startInfo.ArgumentList.Add("-t");
            startInfo.ArgumentList.Add("4");
            startInfo.ArgumentList.Add("--detect-language");

            // Disable GPU for language detection. Each DetectLanguageAsync call spawns a
            // fresh process that must load the model + compile GPU shaders from scratch.
            // For short chunks (~30s) the GPU init overhead exceeds the detection work
            // itself (~21s/chunk with GPU vs ~15s/chunk CPU-only). Transcription still
            // uses GPU where available.
            startInfo.ArgumentList.Add("--no-gpu");

            _logger.LogDebug("Running: {Executable} {Arguments}", whisperExecutable,
                string.Join(" ", startInfo.ArgumentList));

            using var process = new Process { StartInfo = startInfo };
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            process.OutputDataReceived += (_, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) errorBuilder.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(120));
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw;
            }

            // Flush async stdout/stderr pipe buffers
            process.WaitForExit();

            // A crash here (e.g. SIGILL/exit 132 on a non-AVX2 CPU running an AVX2 build) produces
            // truncated output and would otherwise be misreported as "could not detect language".
            // Surface the precise, actionable cause first — same diagnosis as the transcription path.
            var exitFailure = DescribeWhisperExitFailure(process.ExitCode, errorBuilder.ToString());
            if (exitFailure != null) throw new InvalidOperationException(exitFailure);

            var allOutput = outputBuilder.ToString() + "\n" + errorBuilder.ToString();

            // Parse language from whisper output. Handles multiple formats:
            // "Detected language: en" (--detect-language mode)
            // "whisper_full_with_state: auto-detected language: en (p = 0.978)"
            var match = Regex.Match(allOutput,
                @"(?:auto-)?[Dd]etected language:\s*([\w]+(?:\s[\w]+)*)(?:\s*\(p\s*=\s*([\d.]+)\))?");
            if (match.Success)
            {
                var lang = NormalizeLangName(match.Groups[1].Value);
                var prob = match.Groups[2].Success
                    ? float.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)
                    : 0.5f;
                _logger.LogInformation("Detected language: {Language} (p={Probability:F3})", lang, prob);
                return (lang, prob);
            }

            // Fallback: some builds print probability table to stdout, e.g. "en    0.9780"
            var probMatch = Regex.Match(allOutput, @"^\s*([a-z]{2})\s+([\d.]+)\s*$", RegexOptions.Multiline);
            if (probMatch.Success)
            {
                var lang = probMatch.Groups[1].Value;
                var prob = float.Parse(probMatch.Groups[2].Value, CultureInfo.InvariantCulture);
                _logger.LogInformation("Detected language (fallback parse): {Language} (p={Probability:F3})", lang, prob);
                return (lang, prob);
            }

            _logger.LogWarning("Could not parse language from whisper output:\n{Output}", allOutput);
            throw new InvalidOperationException(
                "Could not detect language. Ensure your whisper.cpp build supports --detect-language (v1.5.0+).");
        }

        /// <summary>
        /// Normalizes whisper language names (e.g. "english" → "en") to ISO 639-1 codes.
        /// </summary>
        private static string NormalizeLangName(string lang)
        {
            return lang.ToLowerInvariant() switch
            {
                "english" => "en",
                "spanish" => "es",
                "french" => "fr",
                "german" => "de",
                "italian" => "it",
                "portuguese" => "pt",
                "russian" => "ru",
                "japanese" => "ja",
                "chinese" => "zh",
                "korean" => "ko",
                "arabic" => "ar",
                "hindi" => "hi",
                "dutch" => "nl",
                "polish" => "pl",
                "turkish" => "tr",
                "swedish" => "sv",
                "danish" => "da",
                "finnish" => "fi",
                "norwegian" => "no",
                "czech" => "cs",
                "romanian" => "ro",
                "hungarian" => "hu",
                "greek" => "el",
                "hebrew" => "he",
                "thai" => "th",
                "ukrainian" => "uk",
                "vietnamese" => "vi",
                "indonesian" => "id",
                "catalan" => "ca",
                "basque" => "eu",
                "galician" => "gl",
                "haitian creole" => "ht",
                // Short codes (2-3 chars) pass through; unknown long names are logged and kept as-is
                // rather than silently truncated to potentially invalid codes
                _ => lang.ToLowerInvariant()
            };
        }

        private static string GetLanguagePrompt(string language)
        {
            return language switch
            {
                "en" => "This is a transcription in English.",
                "es" => "Esta es una transcripción en español.",
                "fr" => "Ceci est une transcription en français.",
                "de" => "Dies ist eine Transkription auf Deutsch.",
                "it" => "Questa è una trascrizione in italiano.",
                "pt" => "Esta é uma transcrição em português.",
                "ja" => "これは日本語の書き起こしです。",
                "zh" => "这是中文转录。",
                "ko" => "이것은 한국어 전사입니다.",
                "ru" => "Это транскрипция на русском языке.",
                _ => ""
            };
        }

        /// <summary>
        /// Parses the last end timestamp from an SRT file and returns it in seconds.
        /// Returns 0 if the file is empty or unparseable.
        /// </summary>
        public static double ParseLastSrtTimestamp(string srtContent)
        {
            if (string.IsNullOrWhiteSpace(srtContent)) return 0;

            // Match SRT timestamp lines: "00:12:34,567 --> 00:12:39,890"
            var matches = Regex.Matches(srtContent, @"(\d{2}):(\d{2}):(\d{2}),(\d{3})\s*-->\s*(\d{2}):(\d{2}):(\d{2}),(\d{3})");
            if (matches.Count == 0) return 0;

            var last = matches[matches.Count - 1];
            var hours = int.Parse(last.Groups[5].Value);
            var minutes = int.Parse(last.Groups[6].Value);
            var seconds = int.Parse(last.Groups[7].Value);
            var millis = int.Parse(last.Groups[8].Value);

            return hours * 3600.0 + minutes * 60.0 + seconds + millis / 1000.0;
        }

        /// <summary>
        /// Offsets all timestamps in an SRT string by the given number of seconds.
        /// Also renumbers entries starting from the given startIndex.
        /// </summary>
        public static string OffsetSrt(string srtContent, double offsetSeconds, int startIndex)
        {
            if (string.IsNullOrWhiteSpace(srtContent)) return "";

            var result = new StringBuilder();
            int entryNum = startIndex;

            var lines = srtContent.Split('\n');
            int i = 0;
            while (i < lines.Length)
            {
                var line = lines[i].Trim();

                // Skip empty lines
                if (string.IsNullOrEmpty(line)) { i++; continue; }

                // Skip entry number line (we'll renumber)
                if (int.TryParse(line, out _))
                {
                    i++;
                    if (i >= lines.Length) break;
                    line = lines[i].Trim();
                }

                // Parse timestamp line
                var match = Regex.Match(line, @"(\d{2}:\d{2}:\d{2},\d{3})\s*-->\s*(\d{2}:\d{2}:\d{2},\d{3})");
                if (!match.Success) { i++; continue; }

                var startTs = OffsetTimestamp(match.Groups[1].Value, offsetSeconds);
                var endTs = OffsetTimestamp(match.Groups[2].Value, offsetSeconds);

                result.AppendLine(entryNum.ToString());
                result.AppendLine($"{startTs} --> {endTs}");
                i++;

                // Collect subtitle text lines until empty line
                while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
                {
                    result.AppendLine(lines[i].TrimEnd());
                    i++;
                }
                result.AppendLine();
                entryNum++;
            }

            return result.ToString();
        }

        private static string OffsetTimestamp(string timestamp, double offsetSeconds)
        {
            var match = Regex.Match(timestamp, @"(\d{2}):(\d{2}):(\d{2}),(\d{3})");
            if (!match.Success) return timestamp;

            var totalMs = int.Parse(match.Groups[1].Value) * 3600000
                        + int.Parse(match.Groups[2].Value) * 60000
                        + int.Parse(match.Groups[3].Value) * 1000
                        + int.Parse(match.Groups[4].Value);

            totalMs += (int)(offsetSeconds * 1000);
            if (totalMs < 0) totalMs = 0;

            var h = totalMs / 3600000;
            var m = (totalMs % 3600000) / 60000;
            var s = (totalMs % 60000) / 1000;
            var ms = totalMs % 1000;

            return $"{h:D2}:{m:D2}:{s:D2},{ms:D3}";
        }

        /// <summary>
        /// Returns the highest entry number in an SRT string.
        /// </summary>
        public static int CountSrtEntries(string srtContent)
        {
            if (string.IsNullOrWhiteSpace(srtContent)) return 0;
            var matches = Regex.Matches(srtContent, @"-->"); // Each entry has one --> line
            return matches.Count;
        }

        /// <summary>
        /// Aligns subtitle START times to detected speech onsets. whisper.cpp chains segments
        /// gaplessly (next.start == prev.end), so a subtitle for upcoming speech appears during the
        /// preceding silence. For each entry whose start falls inside a silence gap, this snaps the
        /// start FORWARD to the next speech onset — never backward, never past (end - 0.5s), so a
        /// subtitle keeps at least ~0.5s on screen. End times, text, and ordering are untouched.
        /// </summary>
        /// <param name="srtContent">Raw SRT text.</param>
        /// <param name="speechSegments">Detected speech regions (seconds), each (Start,End); the gaps
        /// between them are silence. May be unsorted; treat defensively. Empty => return input unchanged.</param>
        public static string AlignSrtToSpeech(string srtContent, IReadOnlyList<(double Start, double End)> speechSegments)
        {
            if (string.IsNullOrWhiteSpace(srtContent) || speechSegments == null || speechSegments.Count == 0)
                return srtContent;

            // Work on a sorted copy (by Start) so onset lookups scan in timeline order.
            var segments = speechSegments.OrderBy(s => s.Start).ToList();

            var result = new StringBuilder();

            var lines = srtContent.Split('\n');
            int i = 0;
            while (i < lines.Length)
            {
                var line = lines[i].Trim();

                // Skip empty lines
                if (string.IsNullOrEmpty(line)) { i++; continue; }

                // Preserve the original entry number exactly as found (do NOT renumber).
                string number = "";
                if (int.TryParse(line, out _))
                {
                    number = line;
                    i++;
                    if (i >= lines.Length) break;
                    line = lines[i].Trim();
                }

                // Parse timestamp line
                var match = Regex.Match(line, @"(\d{2}:\d{2}:\d{2},\d{3})\s*-->\s*(\d{2}:\d{2}:\d{2},\d{3})");
                if (!match.Success)
                {
                    // Malformed entry (no timing): pass through unchanged rather than dropping.
                    if (!string.IsNullOrEmpty(number)) result.AppendLine(number);
                    result.AppendLine(line);
                    i++;
                    while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
                    {
                        result.AppendLine(lines[i].TrimEnd());
                        i++;
                    }
                    result.AppendLine();
                    continue;
                }

                var origStartTs = match.Groups[1].Value;
                var origEndTs = match.Groups[2].Value;
                var origStart = SrtTimeToSeconds(origStartTs);
                var origEnd = SrtTimeToSeconds(origEndTs);

                var newStart = origStart;

                // "In speech" test: origStart sits inside some speech segment (with a tiny tolerance).
                bool inSpeech = false;
                foreach (var seg in segments)
                {
                    if (seg.Start - 0.001 <= origStart && origStart <= seg.End + 0.001)
                    {
                        inSpeech = true;
                        break;
                    }
                }

                if (!inSpeech)
                {
                    // In a silence gap: snap forward to the next speech onset, if any.
                    double? onset = null;
                    foreach (var seg in segments)
                    {
                        if (seg.Start > origStart)
                        {
                            onset = seg.Start;
                            break;
                        }
                    }

                    if (onset.HasValue)
                    {
                        newStart = onset.Value;

                        // Keep at least ~0.5s on screen and never move backward.
                        double maxStart = Math.Max(origStart, origEnd - 0.5);
                        newStart = Math.Min(newStart, maxStart);
                        newStart = Math.Max(newStart, origStart);

                        // Avoid churn: ignore sub-50ms adjustments.
                        if (newStart - origStart < 0.05)
                            newStart = origStart;
                    }
                }

                var startTs = newStart == origStart ? origStartTs : SecondsToSrtTime(newStart);

                if (!string.IsNullOrEmpty(number)) result.AppendLine(number);
                result.AppendLine($"{startTs} --> {origEndTs}");
                i++;

                // Collect subtitle text lines until empty line
                while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]))
                {
                    result.AppendLine(lines[i].TrimEnd());
                    i++;
                }
                result.AppendLine();
            }

            return result.ToString();
        }

        private static double SrtTimeToSeconds(string timestamp)
        {
            var match = Regex.Match(timestamp, @"(\d{2}):(\d{2}):(\d{2}),(\d{3})");
            if (!match.Success) return 0;

            return int.Parse(match.Groups[1].Value) * 3600.0
                 + int.Parse(match.Groups[2].Value) * 60.0
                 + int.Parse(match.Groups[3].Value)
                 + int.Parse(match.Groups[4].Value) / 1000.0;
        }

        private static string SecondsToSrtTime(double seconds)
        {
            if (seconds < 0) seconds = 0;

            // Truncate (not round) to match OffsetTimestamp, so the align and offset passes
            // produce identical millisecond values for the same input.
            var totalMs = (int)(seconds * 1000);

            var h = totalMs / 3600000;
            var m = (totalMs % 3600000) / 60000;
            var s = (totalMs % 60000) / 1000;
            var ms = totalMs % 1000;

            return $"{h:D2}:{m:D2}:{s:D2},{ms:D3}";
        }

        // Maps a non-zero whisper-cli exit code to an actionable error message, or null if the code
        // is not a known fatal-launch failure. Shared by transcription and language detection so both
        // give the same precise diagnosis (e.g. a SIGILL on a non-AVX2 CPU) instead of a generic error.
        internal static string? DescribeWhisperExitFailure(int exitCode, string? stderr)
        {
            if (exitCode == 127)
            {
                var match = Regex.Match(stderr ?? string.Empty, @"error while loading shared libraries:\s*(\S+?):");
                var lib = match.Success ? match.Groups[1].Value : "a shared library";
                return $"whisper-cli could not start: missing {lib}. The binary requires a system library " +
                       $"that isn't present in this container. Install it (e.g. 'apt install libgomp1' for " +
                       $"libgomp.so.1) or switch to a binary variant that doesn't need it. Raw error: {(stderr ?? string.Empty).Trim()}";
            }
            if (exitCode == 132 || exitCode == 134 || exitCode == 135)
            {
                return "whisper-cli crashed on launch with an illegal-instruction error (exit " +
                       $"{exitCode}). This CPU likely lacks an instruction set (such as AVX2) that " +
                       "this binary was built for. Re-download the binary and choose the " +
                       "\"CPU (Compatibility)\" / noavx variant on the plugin setup page, which is built " +
                       $"for CPUs without AVX2. Raw error: {(stderr ?? string.Empty).Trim()}";
            }
            return null;
        }

        /// <summary>
        /// Builds the whisper-cli argument vector shared by every transcription run (the full,
        /// translation, forced-segment, lyrics and resume callers all funnel through one method, so
        /// they emit the same flags). Pure and I/O-free so it is unit-testable — the caller resolves
        /// filesystem-dependent state (VAD model presence, language prompt) and passes the results in.
        /// </summary>
        /// <param name="vadModelPath">Resolved Silero VAD model path, or null/empty to omit VAD.</param>
        /// <param name="langPrompt">Resolved initial prompt, or null/empty to omit it.</param>
        internal static IReadOnlyList<string> BuildTranscribeArguments(
            string modelPath, string audioPath, string language, int threadCount, bool translate,
            string? vadModelPath, string outputPrefix, string? langPrompt)
        {
            var args = new List<string>
            {
                "-m", modelPath,
                "-f", audioPath,
                "-l", language,
            };
            if (threadCount > 0)
            {
                args.Add("-t");
                args.Add(threadCount.ToString(CultureInfo.InvariantCulture));
            }
            args.Add("-mc");
            args.Add("0");
            args.Add("-sns");
            // Make whisper-cli emit "whisper_print_progress_callback: progress = N%" lines to stderr
            // so the ErrorDataReceived handler can parse them and report per-file progress. The flag
            // defaults OFF in whisper.cpp, so without it whisper-cli prints no progress at all, the
            // regex never matches, and the UI progress bar never advances.
            args.Add("--print-progress");
            if (translate)
            {
                args.Add("--translate");
            }
            // Native Silero VAD: makes whisper-cli emit subtitles that start at real speech onset
            // instead of during the preceding silence (whisper.cpp otherwise chains segments
            // gaplessly). Only when a VAD model is configured and present on disk (resolved by caller).
            if (!string.IsNullOrEmpty(vadModelPath))
            {
                args.Add("--vad");
                args.Add("--vad-model");
                args.Add(vadModelPath);
            }
            args.Add("-osrt");
            args.Add("-of");
            args.Add(outputPrefix);
            if (!string.IsNullOrEmpty(langPrompt))
            {
                args.Add("--prompt");
                args.Add(langPrompt);
            }
            return args;
        }

        // Anchored to whisper.cpp's own progress emitters so an unrelated stderr line that merely
        // contains "progress = N%" can't be misread as progress (and thereby silently skipped from the
        // warning log). Both the whisper_print_progress_callback and whisper_full_with_state forms seen
        // across builds begin with a "whisper_" token, hence the family anchor.
        // Compiled (unlike the file's other inline regexes) because this one runs once per stderr
        // line for the whole transcription — the only regex on that hot path.
        private static readonly Regex ProgressRegex = new(
            @"^\s*whisper_\S*:\s*progress\s*=\s*(\d+)%",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// Parses a whisper-cli stderr line of the form
        /// "whisper_print_progress_callback: progress =  42%" into its integer percentage.
        /// Returns false (and pct = 0) for any non-progress line. Pure and unit-testable.
        /// </summary>
        internal static bool TryParseProgress(string? line, out int pct)
        {
            pct = 0;
            if (string.IsNullOrEmpty(line)) return false;
            var m = ProgressRegex.Match(line);
            return m.Success
                && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out pct);
        }

        internal void AppendCustomArgs(ProcessStartInfo startInfo)
        {
            if (string.IsNullOrWhiteSpace(_customArgs)) return;

            var tokens = _customArgs.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                var token = tokens[i];
                var flagName = token.Contains('=') ? token[..token.IndexOf('=')] : token;
                if (DeniedArgs.Contains(flagName))
                {
                    _logger.LogWarning("Skipping denied custom argument: {Arg}", token);
                    if (!token.Contains('=') && i + 1 < tokens.Length && !tokens[i + 1].StartsWith('-'))
                        i++;
                    continue;
                }
                startInfo.ArgumentList.Add(token);
            }
        }

        private string? FindWhisperExecutable()
        {
            if (_resolvedExecutable != null)
            {
                return _resolvedExecutable;
            }

            // When a binary path is explicitly configured, trust it without probing.
            // Probing with --help is unreliable: CUDA/Vulkan builds take several seconds
            // to initialize GPU drivers, exceeding any reasonable timeout and causing
            // the configured path to be silently skipped.
            if (!string.IsNullOrEmpty(_binaryPath))
            {
                if (File.Exists(_binaryPath))
                {
                    _logger.LogInformation("Using configured Whisper executable: {Executable}", _binaryPath);
                    _resolvedExecutable = _binaryPath;
                    return _resolvedExecutable;
                }

                _logger.LogError("Configured Whisper binary not found at: {Path}", _binaryPath);
                return null;
            }

            // No configured path — check the default auto-download location first.
            var dataDir = Plugin.Instance?.DataFolderPath;
            if (!string.IsNullOrEmpty(dataDir))
            {
                var autoDownloaded = Path.Combine(dataDir, "whisper", "whisper-cli");
                if (File.Exists(autoDownloaded))
                {
                    _logger.LogInformation("Found auto-downloaded Whisper executable: {Executable}", autoDownloaded);
                    _resolvedExecutable = autoDownloaded;
                    return _resolvedExecutable;
                }
            }

            // Fallback: discover from PATH via probing.
            // "main" is intentionally excluded: it is a common binary name on Windows
            // (VS build tools, Git, etc.) and could resolve to the wrong executable.
            var candidates = new[] { "whisper-cli", "whisper" };

            foreach (var candidate in candidates)
            {
                try
                {
                    using var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = candidate,
                            Arguments = "--help",
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        }
                    };

                    process.Start();

                    // Drain redirected streams to avoid deadlock — the process may
                    // block if its stdout/stderr buffer fills before WaitForExit returns.
                    process.StandardOutput.ReadToEnd();
                    process.StandardError.ReadToEnd();

                    if (!process.WaitForExit(5000))
                    {
                        _logger.LogDebug("Probe timed out for {Candidate}, skipping", candidate);
                        try { process.Kill(); } catch { }
                        continue;
                    }

                    if (process.ExitCode == 0)
                    {
                        _logger.LogInformation("Found Whisper executable in PATH: {Executable}", candidate);
                        _resolvedExecutable = candidate;
                        return _resolvedExecutable;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("Probe failed for {Candidate}: {Error}", candidate, ex.Message);
                }
            }

            return null;
        }
    }
}
