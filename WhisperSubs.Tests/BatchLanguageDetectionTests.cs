using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WhisperSubs.Controller;
using WhisperSubs.Providers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Covers batched forced-subtitle language detection (issue #5): one whisper-cli run detects many
/// chunks, its stderr is mapped back to the right chunk, and any chunk the batch did not answer
/// falls back to its own single-file detection.
/// </summary>
public class BatchLanguageDetectionTests
{
    // Verbatim whisper.cpp v1.8.4 whisper-cli stderr fragments (captured from a real
    // `whisper-cli -m ggml-base.bin -f a -f b ... -l auto -t 4 --detect-language --no-gpu` run);
    // only the file paths are substituted.
    private const string Header =
        "whisper_init_from_file_with_params_no_state: loading model from 'ggml-base.bin'\n" +
        "whisper_init_with_params_no_state: use gpu    = 0\n" +
        "whisper_model_load: loading model\n" +
        "whisper_init_state: compute buffer (decode) =   96.37 MB\n";

    private static string Processing(string path, string lang, string p) =>
        "\n" +
        "system_info: n_threads = 4 / 14 | WHISPER : COREML = 0 | OPENVINO = 0 | CPU : NEON = 1 | ARM_FMA = 1 | FP16_VA = 1 | MATMUL_INT8 = 1 | DOTPROD = 1 | SME = 1 | ACCELERATE = 1 | REPACK = 1 | \n" +
        "\n" +
        $"main: processing '{path}' (226395 samples, 14.1 sec), 4 threads, 1 processors, 5 beams + best of 5, lang = auto, task = transcribe, timestamps = 1 ...\n" +
        "\n" +
        $"whisper_full_with_state: auto-detected language: {lang} (p = {p})\n";

    private const string Timings =
        "\nwhisper_print_timings:     load time =    50.12 ms\n" +
        "whisper_print_timings:    total time = 14369.58 ms\n";

    private static readonly string[] Paths =
    {
        "/tmp/whispersubs_x/chunk_0000.wav",
        "/tmp/whispersubs_x/chunk_0001.wav",
        "/tmp/whispersubs_x/chunk_0002.wav",
    };

    [Fact]
    public void BuildDetectionArgs_OneFilePerPathInOrder_WithDetectionFlags()
    {
        var args = WhisperProvider.BuildDetectionArgs("/models/ggml-base.bin", Paths);

        Assert.Equal(new[] { "-m", "/models/ggml-base.bin" }, args.Take(2));
        var files = args.Select((a, i) => (a, i)).Where(x => x.a == "-f").Select(x => args[x.i + 1]).ToList();
        Assert.Equal(Paths, files);

        var joined = string.Join(" ", args);
        Assert.Contains("-l auto", joined);
        Assert.Contains("-t 4", joined);
        Assert.Contains("--detect-language", args);
        // Detection runs on the GPU unless the GPU failed its self-check.
        Assert.DoesNotContain("--no-gpu", args);
        // -np would silence the "processing '<path>'" lines the batch parser keys on.
        Assert.DoesNotContain("-np", args);
        Assert.DoesNotContain("--no-prints", args);
    }

    [Fact]
    public void BuildDetectionArgs_SingleFile_MatchesTheHistoricalCommand()
    {
        var args = WhisperProvider.BuildDetectionArgs("m.bin", new[] { "a.wav" });

        Assert.Equal(
            new[] { "-m", "m.bin", "-f", "a.wav", "-l", "auto", "-t", "4", "--detect-language" },
            args);
    }

    [Fact]
    public void BuildDetectionArgs_CpuOnly_AddsNoGpu()
        => Assert.Equal(
            new[] { "-m", "m.bin", "-f", "a.wav", "-l", "auto", "-t", "4", "--detect-language", "--no-gpu" },
            WhisperProvider.BuildDetectionArgs("m.bin", new[] { "a.wav" }, cpuOnly: true));

    [Fact]
    public void Parse_AllDetected_MapsInOrderWithProbabilities()
    {
        var stderr = Header
            + Processing(Paths[0], "es", "0.996483")
            + Processing(Paths[1], "fr", "0.971321")
            + Processing(Paths[2], "de", "0.983489")
            + Timings;

        var r = WhisperProvider.ParseBatchDetection(stderr, Paths).Results;

        Assert.Equal(3, r.Count);
        Assert.Equal(("es", 0.996483f), r[0]);
        Assert.Equal(("fr", 0.971321f), r[1]);
        Assert.Equal(("de", 0.983489f), r[2]);
    }

    [Fact]
    public void Parse_MissingFile_IsNullAtItsIndex_OthersResolve()
    {
        // whisper-cli drops a missing file before loading the model and never prints a processing line for it.
        var stderr = $"error: input file not found '{Paths[1]}'\n" + Header
            + Processing(Paths[0], "en", "0.998014")
            + Processing(Paths[2], "it", "0.986998")
            + Timings;

        var r = WhisperProvider.ParseBatchDetection(stderr, Paths).Results;

        Assert.Equal(("en", 0.998014f), r[0]);
        Assert.Null(r[1]);
        Assert.Equal(("it", 0.986998f), r[2]);
    }

    [Fact]
    public void Parse_UnreadableFile_IsNull_AndDoesNotShiftTheNextResult()
    {
        var stderr = Header
            + Processing(Paths[0], "en", "0.998014")
            + "error: failed to read audio data as wav (Unknown error)\n"
            + $"error: failed to read audio file '{Paths[1]}'\n"
            + Processing(Paths[2], "es", "0.996483")
            + Timings;

        var r = WhisperProvider.ParseBatchDetection(stderr, Paths).Results;

        Assert.Equal(("en", 0.998014f), r[0]);
        Assert.Null(r[1]);
        Assert.Equal(("es", 0.996483f), r[2]);
    }

    [Fact]
    public void Parse_TruncatedRun_LeavesLaterFilesNull()
    {
        // whisper_full failing mid-batch makes whisper-cli print this and exit 10: later files never run.
        var paths = Paths.Concat(new[] { "/tmp/whispersubs_x/chunk_0003.wav" }).ToArray();
        var stderr = Header
            + Processing(paths[0], "en", "0.998014")
            + Processing(paths[1], "es", "0.996483")
            + $"\nsystem_info: n_threads = 4 / 14 | CPU : NEON = 1 | \n\nmain: processing '{paths[2]}' (171165 samples, 10.7 sec), 4 threads, 1 processors, 5 beams + best of 5, lang = auto, task = transcribe, timestamps = 1 ...\n"
            + "whisper-cli: failed to process audio\n";

        var r = WhisperProvider.ParseBatchDetection(stderr, paths).Results;

        Assert.Equal(("en", 0.998014f), r[0]);
        Assert.Equal(("es", 0.996483f), r[1]);
        Assert.Null(r[2]);
        Assert.Null(r[3]);
    }

    [Fact]
    public void Parse_DetectionWithoutProcessingLine_IsIgnored()
    {
        var stderr = Header
            + "whisper_full_with_state: auto-detected language: ru (p = 0.900000)\n"
            + Processing(Paths[0], "en", "0.998014")
            // A second detection line for the same file must not spill onto the next file.
            + "whisper_full_with_state: auto-detected language: ja (p = 0.800000)\n"
            + Processing(Paths[1], "fr", "0.971321");

        var r = WhisperProvider.ParseBatchDetection(stderr, Paths).Results;

        Assert.Equal(("en", 0.998014f), r[0]);
        Assert.Equal(("fr", 0.971321f), r[1]);
        Assert.Null(r[2]);
    }

    [Fact]
    public void Parse_LanguageName_IsNormalised()
    {
        var stderr = Header + Processing(Paths[0], "spanish", "0.91") + Processing(Paths[1], "haitian creole", "0.5");

        var r = WhisperProvider.ParseBatchDetection(stderr, Paths).Results;

        Assert.Equal(("es", 0.91f), r[0]);
        Assert.Equal(("ht", 0.5f), r[1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Parse_EmptyStderr_AllNullWithInputLength(string? stderr)
    {
        var r = WhisperProvider.ParseBatchDetection(stderr, Paths).Results;

        Assert.Equal(3, r.Count);
        Assert.All(r, x => Assert.Null(x));
    }

    [Fact]
    public void Parse_PathWithSpaceAndApostrophe_StillMatches()
    {
        var paths = new[] { "/tmp/my dir/l'été/chunk_0000.wav", "/tmp/it's here/chunk_0001.wav" };
        var stderr = Header + Processing(paths[0], "fr", "0.97") + Processing(paths[1], "it", "0.95");

        var r = WhisperProvider.ParseBatchDetection(stderr, paths).Results;

        Assert.Equal(("fr", 0.97f), r[0]);
        Assert.Equal(("it", 0.95f), r[1]);
    }

    [Fact]
    public void Parse_CrLfLineEndings_StillMatch()
    {
        var stderr = (Header + Processing(Paths[0], "de", "0.98")).Replace("\n", "\r\n");

        var r = WhisperProvider.ParseBatchDetection(stderr, Paths).Results;

        Assert.Equal(("de", 0.98f), r[0]);
    }

    [Fact]
    public void Parse_SamePathTwice_FillsBothInOrder()
    {
        var paths = new[] { Paths[0], Paths[0] };
        var stderr = Header + Processing(Paths[0], "en", "0.9") + Processing(Paths[0], "es", "0.8");

        var r = WhisperProvider.ParseBatchDetection(stderr, paths).Results;

        Assert.Equal(("en", 0.9f), r[0]);
        Assert.Equal(("es", 0.8f), r[1]);
    }

    // ---- Pairing by order when the printed path does not round-trip ----

    // A temp directory under a non-ASCII user name, and what whisper-cli prints for it when argv went
    // through the ANSI code page and stderr was decoded differently: the directory is mangled, the
    // ASCII file name survives.
    private static readonly string[] WinPaths =
    {
        @"C:\Users\José\AppData\Local\Temp\whispersubs_x\chunk_0000.wav",
        @"C:\Users\José\AppData\Local\Temp\whispersubs_x\chunk_0001.wav",
        @"C:\Users\José\AppData\Local\Temp\whispersubs_x\chunk_0002.wav",
    };

    private static string Mangled(string path) => path.Replace("José", "JosÃ©");

    [Fact]
    public void Parse_DirectoryDoesNotRoundTrip_PairsByOrder()
    {
        var stderr = Header
            + Processing(Mangled(WinPaths[0]), "es", "0.99")
            + Processing(Mangled(WinPaths[1]), "fr", "0.98")
            + Processing(Mangled(WinPaths[2]), "de", "0.97")
            + Timings;

        var r = WhisperProvider.ParseBatchDetection(stderr, WinPaths);

        Assert.Equal(new (string, float)?[] { ("es", 0.99f), ("fr", 0.98f), ("de", 0.97f) }, r.Results);
        Assert.Null(r.FirstMismatch);
        Assert.Null(r.FirstUnanswered);
    }

    [Fact]
    public void Parse_NothingOfThePathRoundTrips_StillPairsByOrder()
    {
        // Not even the file name matches (every candidate looks the same), so only the order can pair.
        var stderr = Header
            + Processing(@"C:\?\?.wav", "es", "0.99")
            + Processing(@"C:\?\?.wav", "fr", "0.98")
            + Processing(@"C:\?\?.wav", "de", "0.97");

        var r = WhisperProvider.ParseBatchDetection(stderr, WinPaths);

        Assert.Equal(new (string, float)?[] { ("es", 0.99f), ("fr", 0.98f), ("de", 0.97f) }, r.Results);
    }

    [Fact]
    public void Parse_MangledPaths_UnreadableFile_DoesNotShiftTheNextResult()
    {
        // An unreadable file prints its own line in the loop instead of a processing line: it takes its
        // turn in the order, so the next file's answer stays on the next file.
        var stderr = Header
            + Processing(@"C:\?\?.wav", "en", "0.99")
            + "error: failed to read audio file 'C:\\?\\?.wav'\n"
            + Processing(@"C:\?\?.wav", "es", "0.98");

        var r = WhisperProvider.ParseBatchDetection(stderr, WinPaths);

        Assert.Equal(("en", 0.99f), r.Results[0]);
        Assert.Null(r.Results[1]);
        Assert.Equal(("es", 0.98f), r.Results[2]);
    }

    [Fact]
    public void Parse_MangledMissingFile_IsPlacedByItsFileName()
    {
        var stderr = $"error: input file not found '{Mangled(WinPaths[0])}'\n" + Header
            + Processing(Mangled(WinPaths[1]), "fr", "0.98")
            + Processing(Mangled(WinPaths[2]), "de", "0.97");

        var r = WhisperProvider.ParseBatchDetection(stderr, WinPaths);

        Assert.Null(r.Results[0]);
        Assert.Equal(("fr", 0.98f), r.Results[1]);
        Assert.Equal(("de", 0.97f), r.Results[2]);
    }

    [Fact]
    public void Parse_MissingFileThatCannotBePlaced_AnswersNothingRatherThanGuess()
    {
        // Some file was dropped up front, but nothing says which, so no order-based pairing is safe.
        var stderr = "error: input file not found 'C:\\?\\?.wav'\n" + Header
            + Processing(@"C:\?\?.wav", "fr", "0.98")
            + Processing(@"C:\?\?.wav", "de", "0.97");

        var r = WhisperProvider.ParseBatchDetection(stderr, WinPaths);

        Assert.All(r.Results, x => Assert.Null(x));
        Assert.StartsWith("main: processing 'C:\\?\\?.wav'", r.FirstUnanswered);
    }

    [Fact]
    public void Parse_PathAndOrderDisagree_PathWins_ThenOrderIsNoLongerTrusted()
    {
        // File 1 vanished without a line we know. The exact path of file 2 proves where the run really is;
        // after that a line whose path matches nothing is not paired by an order already shown wrong.
        var paths = Paths.Concat(new[] { "/tmp/whispersubs_x/chunk_0003.wav" }).ToArray();
        var stderr = Header
            + Processing(paths[0], "en", "0.99")
            + Processing(paths[2], "es", "0.98")
            + Processing("/tmp/whispersubs_?/?.wav", "fr", "0.97");

        var r = WhisperProvider.ParseBatchDetection(stderr, paths);

        Assert.Equal(new (string, float)?[] { ("en", 0.99f), null, ("es", 0.98f), null }, r.Results);
        Assert.Contains(paths[1], r.FirstMismatch);
        Assert.Contains(paths[2], r.FirstMismatch);
        Assert.StartsWith("main: processing '/tmp/whispersubs_?/?.wav'", r.FirstUnanswered);
    }

    [Fact]
    public void Parse_PartialAnswer_KeepsTheAnswers_AndNamesTheFirstUnansweredLine()
    {
        var stderr = Header
            + Processing(Paths[0], "en", "0.99")
            + $"\nmain: processing '{Paths[1]}' (1 samples, 0.1 sec), 4 threads ...\n"
            + Processing(Paths[2], "es", "0.98");

        var r = WhisperProvider.ParseBatchDetection(stderr, Paths);

        Assert.Equal(new (string, float)?[] { ("en", 0.99f), null, ("es", 0.98f) }, r.Results);
        Assert.Equal($"main: processing '{Paths[1]}' (1 samples, 0.1 sec), 4 threads ...", r.FirstUnanswered);
        Assert.Null(r.FirstMismatch);
    }

    [Fact]
    public void DetectionFileTimeout_KeepsTheHistoricalPerFileCap()
    {
        Assert.Equal(TimeSpan.FromSeconds(300), WhisperProvider.DefaultDetectionFileTimeout);
        Assert.Equal(TimeSpan.FromSeconds(300), new WhisperProvider(NullLogger<WhisperProvider>.Instance, "m.bin").DetectionFileTimeout);
    }

    [Theory]
    [InlineData("main: processing '/tmp/a.wav' (226395 samples, 14.1 sec), 4 threads, 1 processors, 5 beams + best of 5, lang = auto, task = transcribe, timestamps = 1 ...", true)]
    [InlineData("whisper_full_with_state: auto-detected language: en (p = 0.998014)", false)]
    [InlineData("error: failed to read audio file '/tmp/a.wav'", false)]
    [InlineData(null, false)]
    public void IsProcessingLine_OnlyTheNextFileLine(string? line, bool expected)
    {
        Assert.Equal(expected, WhisperProvider.IsProcessingLine(line));
    }

    [Theory]
    [InlineData("en", 0.99f, "en", false)]
    [InlineData("EN", 0.99f, "en", false)]
    [InlineData("es", 0.3f, "en", true)]
    [InlineData("es", 0.29f, "en", false)]
    [InlineData("ES", 0.9f, "es", false)]
    public void IsForeignDetection_TruthTable(string detected, float p, string primary, bool expected)
    {
        Assert.Equal(expected, SubtitleManager.IsForeignDetection(detected, p, primary));
    }

    // ---- DetectBatchOrNullsAsync: the fallback contract ----

    private static Func<IReadOnlyList<string>, CancellationToken, Task<BatchDetectionResult>> Detector(
        Func<IReadOnlyList<string>, IReadOnlyList<(string Language, float Probability)?>> answer, List<IReadOnlyList<string>>? calls = null)
        => (paths, _) =>
        {
            calls?.Add(paths);
            return Task.FromResult(new BatchDetectionResult(answer(paths)));
        };

    [Fact]
    public async Task Batch_SkipsFailedExtractions_AndAlignsResults()
    {
        var calls = new List<IReadOnlyList<string>>();
        var chunkPaths = new string?[] { "a.wav", null, "c.wav" };

        var r = (await SubtitleManager.DetectBatchOrNullsAsync(
            Detector(p => p.Select(x => ((string, float)?)(x == "a.wav" ? ("en", 0.9f) : ("es", 0.8f))).ToList(), calls),
            chunkPaths, NullLogger.Instance, "item", CancellationToken.None)).Results;

        Assert.Single(calls);
        Assert.Equal(new[] { "a.wav", "c.wav" }, calls[0]);
        Assert.Equal(("en", 0.9f), r[0]);
        Assert.Null(r[1]);
        Assert.Equal(("es", 0.8f), r[2]);
    }

    [Fact]
    public async Task Batch_NoDetector_AllNull_SoEveryChunkFallsBack()
    {
        var r = (await SubtitleManager.DetectBatchOrNullsAsync(
            null, new string?[] { "a.wav", "b.wav" }, NullLogger.Instance, "item", CancellationToken.None)).Results;

        Assert.Equal(2, r.Length);
        Assert.All(r, x => Assert.Null(x));
    }

    [Fact]
    public async Task Batch_NothingExtracted_DoesNotCallDetector()
    {
        var calls = new List<IReadOnlyList<string>>();
        var r = (await SubtitleManager.DetectBatchOrNullsAsync(
            Detector(p => p.Select(_ => ((string, float)?)("en", 1f)).ToList(), calls),
            new string?[] { null, null }, NullLogger.Instance, "item", CancellationToken.None)).Results;

        Assert.Empty(calls);
        Assert.All(r, x => Assert.Null(x));
    }

    [Fact]
    public async Task Batch_DetectorThrows_AllNull_SoEveryChunkFallsBack()
    {
        var r = (await SubtitleManager.DetectBatchOrNullsAsync(
            (_, _) => throw new WhisperLaunchException(132, "illegal instruction"),
            new string?[] { "a.wav", "b.wav" }, NullLogger.Instance, "item", CancellationToken.None)).Results;

        Assert.All(r, x => Assert.Null(x));
    }

    [Fact]
    public async Task Batch_TimeoutNotCallerCancel_AllNull()
    {
        // The batch's own timeout surfaces as an OperationCanceledException while the caller's token is
        // still live: that is a batch failure (fall back), not a caller cancellation.
        var r = (await SubtitleManager.DetectBatchOrNullsAsync(
            (_, _) => throw new OperationCanceledException(),
            new string?[] { "a.wav" }, NullLogger.Instance, "item", CancellationToken.None)).Results;

        Assert.Null(r[0]);
    }

    [Fact]
    public async Task Batch_CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SubtitleManager.DetectBatchOrNullsAsync(
            (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult(new BatchDetectionResult(new (string, float)?[1])); },
            new string?[] { "a.wav" }, NullLogger.Instance, "item", cts.Token));
    }

    [Fact]
    public async Task Batch_ShortOrPartialAnswer_LeavesTheRestNull()
    {
        var r = (await SubtitleManager.DetectBatchOrNullsAsync(
            Detector(_ => new (string, float)?[] { ("fr", 0.7f) }),
            new string?[] { "a.wav", "b.wav", "c.wav" }, NullLogger.Instance, "item", CancellationToken.None)).Results;

        Assert.Equal(("fr", 0.7f), r[0]);
        Assert.Null(r[1]);
        Assert.Null(r[2]);
    }

    // ---- DetectForeignChunksAsync: the batch loop that maps results back to chunk indices ----

    private static (double Start, double End) Chunk(int i, bool shortChunk = false)
        => (i * 10.0, (i * 10.0) + (shortChunk ? 0.5 : 5.0));

    private static string PathOf(int i) => $"chunk_{i:D4}.wav";

    private static int IndexOf(string path) => int.Parse(path.Substring(6, 4));

    // Every third chunk is French, the rest English, so a result landing on the wrong chunk shows up.
    private static string LanguageOf(int i) => i % 3 == 0 ? "fr" : "en";

    [Fact]
    public async Task Foreign_MoreThanOneBatch_EveryChunkGetsItsOwnLanguage()
    {
        const int count = 75; // three batches of 32
        var shortOnes = new HashSet<int> { 5, 40, 41, 70 };
        var chunks = Enumerable.Range(0, count).Select(i => Chunk(i, shortOnes.Contains(i))).ToList();
        var extracted = new List<int>();
        var batches = new List<IReadOnlyList<string>>();
        var singles = new List<string>();

        var r = await SubtitleManager.DetectForeignChunksAsync(
            chunks, "en",
            (i, _) => { extracted.Add(i); return Task.FromResult(PathOf(i)); },
            (paths, _) =>
            {
                batches.Add(paths);
                // Leave every 7th chunk unanswered so it takes the single-file fallback.
                return Task.FromResult(new BatchDetectionResult(paths
                    .Select(p => IndexOf(p) % 7 == 0 ? null : ((string, float)?)(LanguageOf(IndexOf(p)), 0.9f))
                    .ToList()));
            },
            (path, _) => { singles.Add(path); return Task.FromResult((LanguageOf(IndexOf(path)), 0.9f)); },
            NullLogger.Instance, "item", CancellationToken.None);

        var eligible = Enumerable.Range(0, count).Where(i => !shortOnes.Contains(i)).ToList();
        Assert.Equal(eligible, extracted);
        Assert.Equal(new[] { 32, 32, eligible.Count - 64 }, batches.Select(b => b.Count));
        Assert.Equal(eligible.Select(PathOf), batches.SelectMany(b => b));
        Assert.Equal(eligible.Where(i => i % 7 == 0).Select(PathOf), singles);

        Assert.False(r.Aborted);
        Assert.Equal(eligible.Count, r.SuccessfulDetections);
        Assert.Equal(
            eligible.Where(i => LanguageOf(i) == "fr").Select(i => (chunks[i].Start, chunks[i].End, "fr")),
            r.ForeignChunks);
    }

    [Fact]
    public async Task Foreign_NoBatchDetector_DetectsEachChunkOnItsOwn()
    {
        var chunks = Enumerable.Range(0, 40).Select(i => Chunk(i)).ToList();
        var singles = new List<string>();

        var r = await SubtitleManager.DetectForeignChunksAsync(
            chunks, "en",
            (i, _) => Task.FromResult(PathOf(i)),
            null,
            (path, _) => { singles.Add(path); return Task.FromResult((LanguageOf(IndexOf(path)), 0.9f)); },
            NullLogger.Instance, "item", CancellationToken.None);

        Assert.Equal(Enumerable.Range(0, 40).Select(PathOf), singles);
        Assert.Equal(Enumerable.Range(0, 40).Count(i => LanguageOf(i) == "fr"), r.ForeignChunks.Count);
    }

    [Fact]
    public async Task Foreign_ConsecutiveFailuresCarryAcrossTheBatchBoundary()
    {
        // Chunks 30 and 31 (end of batch one) fail to extract, chunk 32 (start of batch two) fails to
        // detect: three in a row, so the item aborts on chunk 32 and nothing after it is looked at.
        var chunks = Enumerable.Range(0, 64).Select(i => Chunk(i)).ToList();
        var singles = new List<string>();

        var r = await SubtitleManager.DetectForeignChunksAsync(
            chunks, "en",
            (i, _) => i is 30 or 31 ? throw new IOException($"extract {i}") : Task.FromResult(PathOf(i)),
            (paths, _) => Task.FromResult(new BatchDetectionResult(paths
                .Select(p => IndexOf(p) == 32 ? null : ((string, float)?)("en", 0.9f)).ToList())),
            (path, _) =>
            {
                singles.Add(path);
                return IndexOf(path) == 32 ? throw new InvalidOperationException("detect 32") : Task.FromResult(("en", 0.9f));
            },
            NullLogger.Instance, "item", CancellationToken.None);

        Assert.True(r.Aborted);
        Assert.Equal(3, r.ConsecutiveFailures);
        Assert.Equal("detect 32", r.LastFailure!.Message);
        Assert.Equal(30, r.SuccessfulDetections);
        Assert.Equal(new[] { PathOf(32) }, singles);
    }

    [Fact]
    public async Task Foreign_ASuccessAcrossTheBoundaryResetsTheCount()
    {
        var chunks = Enumerable.Range(0, 64).Select(i => Chunk(i)).ToList();
        var failing = new HashSet<int> { 30, 31, 33, 34 };

        var r = await SubtitleManager.DetectForeignChunksAsync(
            chunks, "en",
            (i, _) => failing.Contains(i) ? throw new IOException($"extract {i}") : Task.FromResult(PathOf(i)),
            (paths, _) => Task.FromResult(new BatchDetectionResult(paths
                .Select(p => ((string, float)?)(LanguageOf(IndexOf(p)), 0.9f)).ToList())),
            (_, _) => throw new InvalidOperationException("no single run expected"),
            NullLogger.Instance, "item", CancellationToken.None);

        Assert.False(r.Aborted);
        Assert.Equal(60, r.SuccessfulDetections);
        Assert.Equal(Enumerable.Range(0, 64).Count(i => !failing.Contains(i) && LanguageOf(i) == "fr"), r.ForeignChunks.Count);
    }

    // ---- The latch: a batch that finishes but answers nothing turns batching off for the item ----

    private sealed class ListLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }

    private static async Task<(int BatchCalls, List<string> Singles, ListLogger Log, SubtitleManager.ForeignDetectionResult Result)> RunForeign(
        int count, Func<IReadOnlyList<string>, BatchDetectionResult> batch)
    {
        var chunks = Enumerable.Range(0, count).Select(i => Chunk(i)).ToList();
        var singles = new List<string>();
        var log = new ListLogger();
        int batchCalls = 0;

        var r = await SubtitleManager.DetectForeignChunksAsync(
            chunks, "en",
            (i, _) => Task.FromResult(PathOf(i)),
            (paths, _) => { batchCalls++; return Task.FromResult(batch(paths)); },
            (path, _) => { singles.Add(path); return Task.FromResult((LanguageOf(IndexOf(path)), 0.9f)); },
            log, "item", CancellationToken.None);
        return (batchCalls, singles, log, r);
    }

    [Fact]
    public async Task Foreign_BatchAnsweringNothing_TurnsBatchingOffForTheItem_AndLogsOnce()
    {
        const string line = "main: processing 'C:\\?\\chunk_0000.wav' (1 samples, 0.1 sec)";
        var (batchCalls, singles, log, r) = await RunForeign(100,
            paths => new BatchDetectionResult(new (string, float)?[paths.Count], FirstUnanswered: line));

        Assert.Equal(1, batchCalls);
        Assert.Equal(Enumerable.Range(0, 100).Select(PathOf), singles);
        var error = Assert.Single(log.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains(line, error.Message);
        Assert.False(r.Aborted);
        Assert.Equal(100, r.SuccessfulDetections);
    }

    [Fact]
    public async Task Foreign_TimedOutBatchAnsweringNothing_KeepsBatching()
    {
        // A stall is one slow file, not output that cannot be mapped: the next batch may well be fine.
        var (batchCalls, singles, log, _) = await RunForeign(40,
            paths => new BatchDetectionResult(new (string, float)?[paths.Count], TimedOut: true));

        Assert.Equal(2, batchCalls);
        Assert.Equal(40, singles.Count);
        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task Foreign_PartialAnswer_KeepsBatching_AndOnlyTheGapsRunAlone()
    {
        var (batchCalls, singles, log, r) = await RunForeign(40,
            paths => new BatchDetectionResult(paths
                .Select(p => IndexOf(p) % 2 == 0 ? ((string, float)?)(LanguageOf(IndexOf(p)), 0.9f) : null).ToList()));

        Assert.Equal(2, batchCalls);
        Assert.Equal(Enumerable.Range(0, 40).Where(i => i % 2 == 1).Select(PathOf), singles);
        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Error);
        Assert.Equal(40, r.SuccessfulDetections);
    }

    // ---- DetectLanguagesAsync against a fake whisper-cli: timeout and crash keep what was answered ----

    private static string FakeLine(string path, string lang) =>
        $"echo \"main: processing '{path}' (226395 samples, 14.1 sec), 4 threads, 1 processors, 5 beams + best of 5, lang = auto, task = transcribe, timestamps = 1 ...\" >&2\n" +
        $"echo \"whisper_full_with_state: auto-detected language: {lang} (p = 0.990000)\" >&2\n";

    private static (WhisperProvider Provider, string[] Paths) FakeWhisper(Func<string[], string> body, TimeSpan fileTimeout)
    {
        var root = Path.Combine(Path.GetTempPath(), "whispersubs-batch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var model = Path.Combine(root, "model.bin");
        File.WriteAllText(model, "x");
        var paths = Enumerable.Range(0, 4).Select(i => Path.Combine(root, $"chunk_{i:D4}.wav")).ToArray();
        var script = Path.Combine(root, "fake-whisper-cli.sh");
        File.WriteAllText(script, "#!/bin/sh\n" + body(paths));
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var provider = new WhisperProvider(NullLogger<WhisperProvider>.Instance, model, script) { DetectionFileTimeout = fileTimeout };
        return (provider, paths);
    }

    [Fact]
    public async Task Fake_TimeoutAfterTwoAnswers_KeepsThemAndReturnsPromptly()
    {
        if (OperatingSystem.IsWindows()) return;
        var (provider, paths) = FakeWhisper(p4 =>
            FakeLine(p4[0], "es") + FakeLine(p4[1], "fr") +
            $"echo \"main: processing '{p4[2]}' (1 samples, 0.1 sec)\" >&2\nexec sleep 60\n", TimeSpan.FromSeconds(2));

        var clock = Stopwatch.StartNew();
        var r = (await provider.DetectLanguagesAsync(paths, CancellationToken.None)).Results;

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"took {clock.Elapsed}");
        Assert.Equal(("es", 0.99f), r[0]);
        Assert.Equal(("fr", 0.99f), r[1]);
        Assert.Null(r[2]);
        Assert.Null(r[3]);
    }

    [Fact]
    public async Task Fake_PrintedPathsDoNotRoundTrip_StillPairByOrder()
    {
        // whisper-cli prints each path as something that matches none of the argv paths.
        if (OperatingSystem.IsWindows()) return;
        var (provider, paths) = FakeWhisper(p4 =>
            string.Concat(p4.Select((_, i) => FakeLine($"/t?mp/garbled_{i}", i % 2 == 0 ? "es" : "ru"))), TimeSpan.FromSeconds(30));

        var r = (await provider.DetectLanguagesAsync(paths, CancellationToken.None)).Results;

        Assert.Equal(new (string, float)?[] { ("es", 0.99f), ("ru", 0.99f), ("es", 0.99f), ("ru", 0.99f) }, r);
    }

    [Fact]
    public async Task Fake_SlowButMovingBatch_IsNeverKilled()
    {
        // Each file takes 1.2 s and the per-file timer is 3 s, but the whole run takes almost 5 s.
        if (OperatingSystem.IsWindows()) return;
        var (provider, paths) = FakeWhisper(p4 =>
            string.Concat(p4.Select((p, i) =>
                $"echo \"main: processing '{p}' (1 samples, 0.1 sec)\" >&2\nsleep 1.2\n" +
                $"echo \"whisper_full_with_state: auto-detected language: {(i % 2 == 0 ? "de" : "it")} (p = 0.990000)\" >&2\n")), TimeSpan.FromSeconds(3));

        var r = (await provider.DetectLanguagesAsync(paths, CancellationToken.None)).Results;

        Assert.Equal(new (string, float)?[] { ("de", 0.99f), ("it", 0.99f), ("de", 0.99f), ("it", 0.99f) }, r);
    }

    [Fact]
    public async Task Fake_CrashAfterAnswers_KeepsThem()
    {
        if (OperatingSystem.IsWindows()) return;
        var (provider, paths) = FakeWhisper(p4 =>
            FakeLine(p4[0], "en") + FakeLine(p4[1], "ru") + "exit 134\n", TimeSpan.FromSeconds(30));

        var r = (await provider.DetectLanguagesAsync(paths, CancellationToken.None)).Results;

        Assert.Equal(("en", 0.99f), r[0]);
        Assert.Equal(("ru", 0.99f), r[1]);
        Assert.Null(r[2]);
        Assert.Null(r[3]);
    }

    [Fact]
    public async Task Fake_CrashBeforeAnyAnswer_IsALaunchFailure()
    {
        if (OperatingSystem.IsWindows()) return;
        var (provider, paths) = FakeWhisper(_ => "echo 'Illegal instruction' >&2\nexit 132\n", TimeSpan.FromSeconds(30));

        var ex = await Assert.ThrowsAsync<WhisperLaunchException>(() => provider.DetectLanguagesAsync(paths, CancellationToken.None));
        Assert.Equal(132, ex.ExitCode);
    }

    [Fact]
    public async Task Fake_CallerCancellation_StillThrows()
    {
        if (OperatingSystem.IsWindows()) return;
        var (provider, paths) = FakeWhisper(p4 =>
            FakeLine(p4[0], "en") + "exec sleep 60\n", TimeSpan.FromSeconds(30));
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        var clock = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.DetectLanguagesAsync(paths, cts.Token));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), $"took {clock.Elapsed}");
    }
}
