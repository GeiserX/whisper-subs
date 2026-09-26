using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WhisperSubs.Controller;
using WhisperSubs.Providers;
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
        Assert.Contains("--no-gpu", args);
        // -np would silence the "processing '<path>'" lines the batch parser keys on.
        Assert.DoesNotContain("-np", args);
        Assert.DoesNotContain("--no-prints", args);
    }

    [Fact]
    public void BuildDetectionArgs_SingleFile_MatchesTheHistoricalCommand()
    {
        var args = WhisperProvider.BuildDetectionArgs("m.bin", new[] { "a.wav" });

        Assert.Equal(
            new[] { "-m", "m.bin", "-f", "a.wav", "-l", "auto", "-t", "4", "--detect-language", "--no-gpu" },
            args);
    }

    [Fact]
    public void Parse_AllDetected_MapsInOrderWithProbabilities()
    {
        var stderr = Header
            + Processing(Paths[0], "es", "0.996483")
            + Processing(Paths[1], "fr", "0.971321")
            + Processing(Paths[2], "de", "0.983489")
            + Timings;

        var r = WhisperProvider.ParseBatchDetection(stderr, Paths);

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

        var r = WhisperProvider.ParseBatchDetection(stderr, Paths);

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

        var r = WhisperProvider.ParseBatchDetection(stderr, Paths);

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

        var r = WhisperProvider.ParseBatchDetection(stderr, paths);

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

        var r = WhisperProvider.ParseBatchDetection(stderr, Paths);

        Assert.Equal(("en", 0.998014f), r[0]);
        Assert.Equal(("fr", 0.971321f), r[1]);
        Assert.Null(r[2]);
    }

    [Fact]
    public void Parse_LanguageName_IsNormalised()
    {
        var stderr = Header + Processing(Paths[0], "spanish", "0.91") + Processing(Paths[1], "haitian creole", "0.5");

        var r = WhisperProvider.ParseBatchDetection(stderr, Paths);

        Assert.Equal(("es", 0.91f), r[0]);
        Assert.Equal(("ht", 0.5f), r[1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Parse_EmptyStderr_AllNullWithInputLength(string? stderr)
    {
        var r = WhisperProvider.ParseBatchDetection(stderr, Paths);

        Assert.Equal(3, r.Count);
        Assert.All(r, x => Assert.Null(x));
    }

    [Fact]
    public void Parse_PathWithSpaceAndApostrophe_StillMatches()
    {
        var paths = new[] { "/tmp/my dir/l'été/chunk_0000.wav", "/tmp/it's here/chunk_0001.wav" };
        var stderr = Header + Processing(paths[0], "fr", "0.97") + Processing(paths[1], "it", "0.95");

        var r = WhisperProvider.ParseBatchDetection(stderr, paths);

        Assert.Equal(("fr", 0.97f), r[0]);
        Assert.Equal(("it", 0.95f), r[1]);
    }

    [Fact]
    public void Parse_CrLfLineEndings_StillMatch()
    {
        var stderr = (Header + Processing(Paths[0], "de", "0.98")).Replace("\n", "\r\n");

        var r = WhisperProvider.ParseBatchDetection(stderr, Paths);

        Assert.Equal(("de", 0.98f), r[0]);
    }

    [Fact]
    public void Parse_SamePathTwice_FillsBothInOrder()
    {
        var paths = new[] { Paths[0], Paths[0] };
        var stderr = Header + Processing(Paths[0], "en", "0.9") + Processing(Paths[0], "es", "0.8");

        var r = WhisperProvider.ParseBatchDetection(stderr, paths);

        Assert.Equal(("en", 0.9f), r[0]);
        Assert.Equal(("es", 0.8f), r[1]);
    }

    [Fact]
    public void BatchDetectionTimeout_KeepsSingleFileCap_AndGrowsWithCount()
    {
        Assert.Equal(TimeSpan.FromSeconds(300), WhisperProvider.BatchDetectionTimeout(1));
        Assert.Equal(TimeSpan.FromSeconds(300), WhisperProvider.BatchDetectionTimeout(0));
        Assert.Equal(TimeSpan.FromSeconds(330), WhisperProvider.BatchDetectionTimeout(2));
        Assert.Equal(TimeSpan.FromSeconds(300 + (30 * 31)), WhisperProvider.BatchDetectionTimeout(WhisperProvider.DetectionBatchSize));
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

    private static Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyList<(string Language, float Probability)?>>> Detector(
        Func<IReadOnlyList<string>, IReadOnlyList<(string Language, float Probability)?>> answer, List<IReadOnlyList<string>>? calls = null)
        => (paths, _) =>
        {
            calls?.Add(paths);
            return Task.FromResult(answer(paths));
        };

    [Fact]
    public async Task Batch_SkipsFailedExtractions_AndAlignsResults()
    {
        var calls = new List<IReadOnlyList<string>>();
        var chunkPaths = new string?[] { "a.wav", null, "c.wav" };

        var r = await SubtitleManager.DetectBatchOrNullsAsync(
            Detector(p => p.Select(x => ((string, float)?)(x == "a.wav" ? ("en", 0.9f) : ("es", 0.8f))).ToList(), calls),
            chunkPaths, NullLogger.Instance, "item", CancellationToken.None);

        Assert.Single(calls);
        Assert.Equal(new[] { "a.wav", "c.wav" }, calls[0]);
        Assert.Equal(("en", 0.9f), r[0]);
        Assert.Null(r[1]);
        Assert.Equal(("es", 0.8f), r[2]);
    }

    [Fact]
    public async Task Batch_NoDetector_AllNull_SoEveryChunkFallsBack()
    {
        var r = await SubtitleManager.DetectBatchOrNullsAsync(
            null, new string?[] { "a.wav", "b.wav" }, NullLogger.Instance, "item", CancellationToken.None);

        Assert.Equal(2, r.Length);
        Assert.All(r, x => Assert.Null(x));
    }

    [Fact]
    public async Task Batch_NothingExtracted_DoesNotCallDetector()
    {
        var calls = new List<IReadOnlyList<string>>();
        var r = await SubtitleManager.DetectBatchOrNullsAsync(
            Detector(p => p.Select(_ => ((string, float)?)("en", 1f)).ToList(), calls),
            new string?[] { null, null }, NullLogger.Instance, "item", CancellationToken.None);

        Assert.Empty(calls);
        Assert.All(r, x => Assert.Null(x));
    }

    [Fact]
    public async Task Batch_DetectorThrows_AllNull_SoEveryChunkFallsBack()
    {
        var r = await SubtitleManager.DetectBatchOrNullsAsync(
            (_, _) => throw new WhisperLaunchException(132, "illegal instruction"),
            new string?[] { "a.wav", "b.wav" }, NullLogger.Instance, "item", CancellationToken.None);

        Assert.All(r, x => Assert.Null(x));
    }

    [Fact]
    public async Task Batch_TimeoutNotCallerCancel_AllNull()
    {
        // The batch's own timeout surfaces as an OperationCanceledException while the caller's token is
        // still live: that is a batch failure (fall back), not a caller cancellation.
        var r = await SubtitleManager.DetectBatchOrNullsAsync(
            (_, _) => throw new OperationCanceledException(),
            new string?[] { "a.wav" }, NullLogger.Instance, "item", CancellationToken.None);

        Assert.Null(r[0]);
    }

    [Fact]
    public async Task Batch_CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SubtitleManager.DetectBatchOrNullsAsync(
            (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult<IReadOnlyList<(string Language, float Probability)?>>(new (string, float)?[1]); },
            new string?[] { "a.wav" }, NullLogger.Instance, "item", cts.Token));
    }

    [Fact]
    public async Task Batch_ShortOrPartialAnswer_LeavesTheRestNull()
    {
        var r = await SubtitleManager.DetectBatchOrNullsAsync(
            Detector(_ => new (string, float)?[] { ("fr", 0.7f) }),
            new string?[] { "a.wav", "b.wav", "c.wav" }, NullLogger.Instance, "item", CancellationToken.None);

        Assert.Equal(("fr", 0.7f), r[0]);
        Assert.Null(r[1]);
        Assert.Null(r[2]);
    }
}
