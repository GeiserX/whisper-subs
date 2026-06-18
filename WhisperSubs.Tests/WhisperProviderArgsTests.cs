using System.Collections.Generic;
using WhisperSubs.Providers;
using Xunit;

namespace WhisperSubs.Tests;

public class WhisperProviderArgsTests
{
    // Builds the argument vector with sane defaults; the test under each [Fact] varies the one
    // parameter it cares about. Keeps every call site short and the intent obvious.
    private static IReadOnlyList<string> Build(
        string modelPath = "/m/model.bin",
        string audioPath = "/tmp/a.wav",
        string language = "es",
        int threadCount = 0,
        bool translate = false,
        string? vadModelPath = null,
        string outputPrefix = "/tmp/out",
        string? langPrompt = null)
    {
        return WhisperProvider.BuildTranscribeArguments(
            modelPath, audioPath, language, threadCount, translate, vadModelPath, outputPrefix, langPrompt);
    }

    // Asserts that `flag` appears in the vector and is immediately followed by `expectedValue`.
    private static void AssertFlagFollowedBy(IReadOnlyList<string> args, string flag, string expectedValue)
    {
        var idx = IndexOf(args, flag);
        Assert.True(idx >= 0, $"expected flag '{flag}' to be present in: {string.Join(" ", args)}");
        Assert.True(idx + 1 < args.Count, $"flag '{flag}' has no following value in: {string.Join(" ", args)}");
        Assert.Equal(expectedValue, args[idx + 1]);
    }

    private static int IndexOf(IReadOnlyList<string> args, string value)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == value)
            {
                return i;
            }
        }

        return -1;
    }

    // ── BuildTranscribeArguments ────────────────────────────────────────────

    // THE lock: the entire reason this PR exists is to guarantee progress reporting is wired up.
    [Fact]
    public void BuildTranscribeArguments_IncludesPrintProgress()
    {
        var args = Build();
        Assert.Contains("--print-progress", args);
    }

    [Fact]
    public void BuildTranscribeArguments_IncludesCoreFlagsPaired()
    {
        var args = Build(
            modelPath: "/m/model.bin",
            audioPath: "/tmp/a.wav",
            language: "es",
            outputPrefix: "/tmp/out");

        AssertFlagFollowedBy(args, "-m", "/m/model.bin");
        AssertFlagFollowedBy(args, "-f", "/tmp/a.wav");
        AssertFlagFollowedBy(args, "-l", "es");
        Assert.Contains("-osrt", args);
        AssertFlagFollowedBy(args, "-of", "/tmp/out");
    }

    [Fact]
    public void BuildTranscribeArguments_AlwaysIncludesMcAndSns()
    {
        var args = Build();
        AssertFlagFollowedBy(args, "-mc", "0");
        Assert.Contains("-sns", args);
    }

    // Golden vector: pins the exact flag sequence for a fully-populated config (threads + translate
    // + VAD + prompt). whisper-cli is order-insensitive, but this PR's contract is that extracting
    // BuildTranscribeArguments left the emitted command line byte-identical — so lock the order too.
    [Fact]
    public void BuildTranscribeArguments_FullConfig_MatchesCanonicalOrder()
    {
        var args = Build(
            modelPath: "/m/model.bin",
            audioPath: "/tmp/a.wav",
            language: "es",
            threadCount: 8,
            translate: true,
            vadModelPath: "/models/vad.bin",
            outputPrefix: "/tmp/out",
            langPrompt: "hola");

        var expected = new[]
        {
            "-m", "/m/model.bin",
            "-f", "/tmp/a.wav",
            "-l", "es",
            "-t", "8",
            "-mc", "0",
            "-sns",
            "--print-progress",
            "--translate",
            "--vad", "--vad-model", "/models/vad.bin",
            "-osrt",
            "-of", "/tmp/out",
            "--prompt", "hola",
        };
        Assert.Equal(expected, args);
    }

    // Golden vector for the minimal config (no threads / translate / vad / prompt): the optional
    // flags must be absent and the rest in canonical order.
    [Fact]
    public void BuildTranscribeArguments_MinimalConfig_MatchesCanonicalOrder()
    {
        var args = Build(
            modelPath: "/m/model.bin",
            audioPath: "/tmp/a.wav",
            language: "es",
            outputPrefix: "/tmp/out");

        var expected = new[]
        {
            "-m", "/m/model.bin",
            "-f", "/tmp/a.wav",
            "-l", "es",
            "-mc", "0",
            "-sns",
            "--print-progress",
            "-osrt",
            "-of", "/tmp/out",
        };
        Assert.Equal(expected, args);
    }

    [Fact]
    public void BuildTranscribeArguments_ThreadCountZero_OmitsThreadFlag()
    {
        var args = Build(threadCount: 0);
        Assert.DoesNotContain("-t", args);
    }

    [Fact]
    public void BuildTranscribeArguments_ThreadCountPositive_AddsThreadFlagAndValue()
    {
        var args = Build(threadCount: 8);
        AssertFlagFollowedBy(args, "-t", "8");
    }

    [Fact]
    public void BuildTranscribeArguments_Translate_AddsTranslateFlag()
    {
        var args = Build(translate: true);
        Assert.Contains("--translate", args);
    }

    [Fact]
    public void BuildTranscribeArguments_NoTranslate_OmitsTranslateFlag()
    {
        var args = Build(translate: false);
        Assert.DoesNotContain("--translate", args);
    }

    [Fact]
    public void BuildTranscribeArguments_WithVadModel_AddsVadFlagsPaired()
    {
        var args = Build(vadModelPath: "/models/vad.bin");
        Assert.Contains("--vad", args);
        AssertFlagFollowedBy(args, "--vad-model", "/models/vad.bin");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void BuildTranscribeArguments_WithoutVadModel_OmitsVadFlags(string? vadModelPath)
    {
        var args = Build(vadModelPath: vadModelPath);
        Assert.DoesNotContain("--vad", args);
        Assert.DoesNotContain("--vad-model", args);
    }

    [Fact]
    public void BuildTranscribeArguments_WithLangPrompt_AddsPromptPaired()
    {
        var args = Build(langPrompt: "hola");
        AssertFlagFollowedBy(args, "--prompt", "hola");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void BuildTranscribeArguments_WithoutLangPrompt_OmitsPromptFlag(string? langPrompt)
    {
        var args = Build(langPrompt: langPrompt);
        Assert.DoesNotContain("--prompt", args);
    }

    // ── TryParseProgress ────────────────────────────────────────────────────

    [Theory]
    [InlineData("whisper_print_progress_callback: progress =  42%", 42)] // double space from %3d padding
    [InlineData("whisper_print_progress_callback: progress = 0%", 0)]
    [InlineData("whisper_print_progress_callback: progress = 100%", 100)]
    [InlineData("whisper_full_with_state: progress = 33%", 33)]
    public void TryParseProgress_ValidProgressLines_ParsesPercent(string line, int expected)
    {
        var ok = WhisperProvider.TryParseProgress(line, out var pct);
        Assert.True(ok);
        Assert.Equal(expected, pct);
    }

    [Theory]
    [InlineData("whisper_init_state: loading model")]
    [InlineData("system_info: n_threads = 4 | AVX = 1")]
    [InlineData("main: processing 'a.wav' ... progress = 50% pending")] // anchor guard: has "progress = 50%" but no leading whisper_ token
    [InlineData("main: progress = 7%")]
    [InlineData("")]
    public void TryParseProgress_NonProgressLines_ReturnsFalse(string line)
    {
        var ok = WhisperProvider.TryParseProgress(line, out var pct);
        Assert.False(ok);
        Assert.Equal(0, pct);
    }
}
