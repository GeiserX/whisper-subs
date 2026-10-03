using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using WhisperSubs.Providers;
using WhisperSubs.Setup;
using Xunit;

namespace WhisperSubs.Tests;

public class Qwen3ProviderArgsTests
{
    private static IReadOnlyList<string> Build(
        string language = "en",
        int threads = 0,
        string? vadModel = "/vad/silero.bin",
        VadTuning? tuning = null,
        int maxLineLength = 0)
        => Qwen3Provider.BuildArguments(
            "/m/qwen3.gguf", "/tmp/a.wav", language, threads, vadModel, tuning, maxLineLength, "/data/crispasr/cache", "/tmp/out");

    // The exact command line the spike ran on watchtower (CrispASR v0.8.35): --backend qwen3 with the
    // 1.7B weights recognised from the file, VAD from the plugin's Silero model, a byte-counted cap.
    [Fact]
    public void BuildArguments_DefaultConfig_IsTheExactVector()
    {
        Assert.Equal(new[]
        {
            "--backend", "qwen3",
            "-m", "/m/qwen3.gguf",
            "-f", "/tmp/a.wav",
            "-l", "en",
            "--cache-dir", "/data/crispasr/cache",
            "--vad",
            "--vad-model", "/vad/silero.bin",
            "--max-len", "42",
            "--split-on-word",
            "--print-progress",
            "-osrt",
            "-of", "/tmp/out",
        }, Build());
    }

    [Fact]
    public void BuildArguments_EveryOptionSet_IsTheExactVector()
    {
        var tuning = new VadTuning(0.5f, 250, 100, 30f, 30, 0.1f);
        Assert.Equal(new[]
        {
            "--backend", "qwen3",
            "-m", "/m/qwen3.gguf",
            "-f", "/tmp/a.wav",
            "-l", "es",
            "-t", "16",
            "--cache-dir", "/data/crispasr/cache",
            "--vad",
            "--vad-model", "/vad/silero.bin",
            "--vad-threshold", "0.5",
            "--vad-min-speech-duration-ms", "250",
            "--vad-min-silence-duration-ms", "100",
            "--vad-max-speech-duration-s", "30",
            "--vad-speech-pad-ms", "30",
            "--vad-samples-overlap", "0.1",
            "--max-len", "60",
            "--split-on-word",
            "--print-progress",
            "-osrt",
            "-of", "/tmp/out",
        }, Build(language: "es", threads: 16, tuning: tuning, maxLineLength: 60));
    }

    // "auto" or empty: no -l at all, the model identifies the language itself.
    [Theory]
    [InlineData("auto")]
    [InlineData("")]
    [InlineData("  ")]
    public void BuildArguments_AutoLanguage_OmitsTheFlag(string language)
    {
        var args = Build(language: language);
        Assert.DoesNotContain("-l", args);
        Assert.Contains("42", args);
    }

    [Fact]
    public void BuildArguments_LanguageIsLowercasedAndTrimmed()
    {
        var args = Build(language: " ES ");
        var i = Array.IndexOf(args.ToArray(), "-l");
        Assert.Equal("es", args[i + 1]);
    }

    // The forced-subtitle path hands an already-segmented chunk: no VAD flags, no tuning.
    [Fact]
    public void BuildArguments_NoVadModel_OmitsVadAndTuning()
    {
        var tuning = new VadTuning(0.5f, 250, 100, 30f, 30, 0.1f);
        var args = Build(vadModel: null, tuning: tuning);
        Assert.DoesNotContain("--vad", args);
        Assert.DoesNotContain("--vad-model", args);
        Assert.DoesNotContain("--vad-threshold", args);
        Assert.Contains("--max-len", args);
    }

    // crispasr counts --max-len in bytes: two-byte and three-byte scripts get the same letters.
    [Theory]
    [InlineData("en", 0, 42)]
    [InlineData(null, 0, 42)]
    [InlineData("ru", 0, 84)]
    [InlineData("el", 0, 84)]
    [InlineData("ar", 0, 84)]
    [InlineData("zh", 0, 126)]
    [InlineData("ja", 0, 126)]
    [InlineData("ko", 0, 126)]
    [InlineData("ja", 50, 50)]
    [InlineData("en", 60, 60)]
    public void MaxLineLengthFor_ScalesTheDefaultByScriptWidth(string? language, int configured, int expected)
    {
        Assert.Equal(expected, Qwen3Provider.MaxLineLengthFor(language, configured));
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "nl")]
    [InlineData(true, "en")]
    public void EnsureTranscribeOnly_RefusesAnyTranslation(bool translate, string? target)
    {
        Assert.Throws<NotSupportedException>(() => Qwen3Provider.EnsureTranscribeOnly(translate, target));
    }

    [Fact]
    public void EnsureTranscribeOnly_AcceptsAPlainTranscription()
    {
        Qwen3Provider.EnsureTranscribeOnly(false, null);
        Qwen3Provider.EnsureTranscribeOnly(false, "");
    }

    [Fact]
    public async Task TranscribeAsync_TranslateRequest_ThrowsBeforeTouchingTheFilesystem()
    {
        var provider = new Qwen3Provider(NullLogger.Instance, "/nope/crispasr", "/nope/model.gguf", 0, "/nope/vad.bin", null, 0, "/nope/cache");
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.TranscribeAsync("/nope/a.wav", "en", CancellationToken.None, translate: true));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.TranscribeAsync("/nope/a.wav", "en", CancellationToken.None, targetLanguage: "nl"));
    }

    [Fact]
    public async Task TranscribeAsync_MissingBinary_IsAFileNotFound()
    {
        var provider = new Qwen3Provider(NullLogger.Instance, "/nope/crispasr", "/nope/model.gguf", 0, "/nope/vad.bin", null, 0, "/nope/cache");
        var ex = await Assert.ThrowsAsync<System.IO.FileNotFoundException>(() => provider.TranscribeAsync("/nope/a.wav", "en", CancellationToken.None));
        Assert.Contains("crispasr", ex.Message);
    }

    [Fact]
    public async Task DetectLanguageAsync_IsNotSupported()
    {
        var provider = new Qwen3Provider(NullLogger.Instance, "/nope/crispasr", "/nope/model.gguf", 0, "", null, 0, "/nope/cache");
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DetectLanguageAsync("/nope/a.wav", CancellationToken.None));
    }

    [Fact]
    public void Provider_IsNamedAndNeedsAlignmentOptIn()
    {
        var provider = new Qwen3Provider(NullLogger.Instance, "/b", "/m", 0, "/v", null, 0, "/c");
        Assert.Equal("Qwen3-ASR", provider.Name);
        Assert.True(provider.RequiresSpeechAlignmentOptIn);
    }

    // The probe vector: the same engine flags as a real run minus VAD and progress, with -l so no
    // language-identification pass runs on one second of silence.
    [Fact]
    public void BuildQwen3ValidationArguments_IsTheExactProbeCommand()
    {
        Assert.Equal(new[]
        {
            "--backend", "qwen3",
            "-m", "/m/qwen3.gguf",
            "-f", "/p/silence.wav",
            "-l", "en",
            "--cache-dir", "/c",
            "-osrt",
            "-of", "/p/out",
        }, CrispAsrSetupService.BuildQwen3ValidationArguments("/m/qwen3.gguf", "/p/silence.wav", "/c", "/p/out"));
    }

    [Theory]
    [InlineData("/x/qwen3-asr-1.7b-q8_0.gguf", "q8_0")]
    [InlineData("/x/QWEN3-ASR-1.7B-Q4_K.GGUF", "q4_k")]
    [InlineData("/x/canary-1b-v2-q8_0.gguf", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void InstalledQwen3ModelKey_MapsByFileName(string? path, string expected)
    {
        Assert.Equal(expected, CrispAsrSetupService.InstalledQwen3ModelKey(path));
    }

    // Selected + both files present is the only state that transcribes with Qwen3-ASR.
    [Theory]
    [InlineData("qwen3", true, true, true)]
    [InlineData("qwen3", false, true, false)]
    [InlineData("qwen3", true, false, false)]
    [InlineData("whisper", true, true, false)]
    [InlineData("", true, true, false)]
    public void IsQwen3Active_NeedsSelectionAndBothFiles(string engine, bool binaryExists, bool modelExists, bool expected)
    {
        bool Exists(string p) => p == "/b" ? binaryExists : p == "/m" && modelExists;
        Assert.Equal(expected, SubtitleProviderFactory.IsQwen3Active(engine, "/b", "/m", Exists));
    }

    [Fact]
    public void IsQwen3Active_EmptyPaths_IsFalseWithoutTouchingTheFilesystem()
    {
        Assert.False(SubtitleProviderFactory.IsQwen3Active("qwen3", "", "/m", _ => throw new InvalidOperationException("must not stat")));
        Assert.False(SubtitleProviderFactory.IsQwen3Active("qwen3", "/b", null, _ => throw new InvalidOperationException("must not stat")));
    }
}

public class Qwen3AlignerTests
{
    [Fact]
    public void Aligner_IsPinnedWithSizeAndDigest()
    {
        var a = Qwen3Catalog.Aligner;
        Assert.Equal("canary-ctc-aligner-q4_k.gguf", a.FileName);
        Assert.Equal(392167040, a.SizeBytes);
        Assert.Equal("d16dbf18f9a66f59c0ceb61b204caca5dda21742d6e9dc304c9d0518c81ee38c", a.Sha256);
        Assert.Equal(Math.Round(a.SizeBytes / 1_000_000.0), a.SizeMB);
        Assert.Matches("^[0-9a-f]{40}$", Qwen3Catalog.AlignerHuggingFaceRevision);
        Assert.DoesNotContain("/main", Qwen3Catalog.AlignerHuggingFaceBaseUrl);
    }

    // The aligner is Canary's: English plus the 24 Canary targets, nothing else.
    [Theory]
    [InlineData("en", true)]
    [InlineData("es", true)]
    [InlineData("uk", true)]
    [InlineData("zh", false)]
    [InlineData("ja", false)]
    [InlineData("ar", false)]
    [InlineData("auto", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void AlignerSupports_OnlyTheCanaryLanguages(string? language, bool expected)
    {
        Assert.Equal(expected, Qwen3Catalog.AlignerSupports(language));
    }

    [Fact]
    public void AlignerFor_NeedsPathFileAndSupportedLanguage()
    {
        Assert.Equal("/a.gguf", Qwen3Provider.AlignerFor("en", "/a.gguf", _ => true));
        Assert.Equal("/a.gguf", Qwen3Provider.AlignerFor(" ES ", "/a.gguf", _ => true));
        Assert.Null(Qwen3Provider.AlignerFor("ja", "/a.gguf", _ => true));
        Assert.Null(Qwen3Provider.AlignerFor("en", "/a.gguf", _ => false));
        Assert.Null(Qwen3Provider.AlignerFor("en", "", _ => throw new InvalidOperationException("must not stat")));
        Assert.Null(Qwen3Provider.AlignerFor("en", null, _ => throw new InvalidOperationException("must not stat")));
    }

    [Fact]
    public void BuildArguments_WithAligner_AddsTheTwoFlagsBeforeMaxLen()
    {
        var args = Qwen3Provider.BuildArguments(
            "/m/qwen3.gguf", "/tmp/a.wav", "en", 0, "/vad/silero.bin", null, 0, "/data/crispasr/cache", "/tmp/out", "/m/aligner.gguf");
        Assert.Equal(new[]
        {
            "--backend", "qwen3",
            "-m", "/m/qwen3.gguf",
            "-f", "/tmp/a.wav",
            "-l", "en",
            "--cache-dir", "/data/crispasr/cache",
            "--vad",
            "--vad-model", "/vad/silero.bin",
            "-am", "/m/aligner.gguf",
            "--force-aligner",
            "--split-on-punct",
            "--max-len", "42",
            "--split-on-word",
            "--print-progress",
            "-osrt",
            "-of", "/tmp/out",
        }, args);
    }

    [Fact]
    public void BuildArguments_WithoutAligner_HasNoAlignerFlags()
    {
        var args = Qwen3Provider.BuildArguments(
            "/m/qwen3.gguf", "/tmp/a.wav", "en", 0, "/vad/silero.bin", null, 0, "/c", "/tmp/out", null);
        Assert.DoesNotContain("-am", args);
        Assert.DoesNotContain("--force-aligner", args);
        Assert.DoesNotContain("--split-on-punct", args);
    }

    [Fact]
    public void TrimCueLines_DropsTheAlignerLeadingSpaces()
    {
        var srt = "1\n00:00:50,640 --> 00:00:52,400\nAnywhere with you\n\n2\n00:00:52,400 --> 00:00:53,600\n here again, I'm calling the cops. \n";
        Assert.Equal("1\n00:00:50,640 --> 00:00:52,400\nAnywhere with you\n\n2\n00:00:52,400 --> 00:00:53,600\nhere again, I'm calling the cops.\n",
            Qwen3Provider.TrimCueLines(srt));
        Assert.Equal("", Qwen3Provider.TrimCueLines(""));
        Assert.Equal("a\nb", Qwen3Provider.TrimCueLines("a\r\nb"));
    }

    [Fact]
    public void FreshConfig_UsesTheAlignerOnceInstalled()
    {
        var config = new WhisperSubs.Configuration.PluginConfiguration();
        Assert.True(config.Qwen3UseAligner);
        Assert.Equal("", config.Qwen3AlignerModelPath);
    }
}
