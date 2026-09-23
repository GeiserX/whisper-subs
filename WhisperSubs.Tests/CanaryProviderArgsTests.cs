using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using WhisperSubs.Providers;
using Xunit;

namespace WhisperSubs.Tests;

public class CanaryProviderArgsTests
{
    private static IReadOnlyList<string> Build(
        int threads = 0,
        VadTuning? tuning = null,
        int maxLineLength = 0,
        string target = "nl")
        => CanaryProvider.BuildArguments(
            "/m/canary.gguf", "/tmp/a.wav", target, threads, "/vad/silero.bin", tuning, maxLineLength, "/data/crispasr/cache", "/tmp/out");

    // The exact command line from the design, plus -l en (without it crispasr fetches a
    // language-ID model even when -sl is given).
    [Fact]
    public void BuildArguments_DefaultConfig_IsTheExactVector()
    {
        Assert.Equal(new[]
        {
            "--backend", "canary",
            "-m", "/m/canary.gguf",
            "-f", "/tmp/a.wav",
            "-l", "en",
            "-sl", "en",
            "-tl", "nl",
            "--no-auto-aligner",
            "--cache-dir", "/data/crispasr/cache",
            "--vad",
            "--vad-model", "/vad/silero.bin",
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
            "--backend", "canary",
            "-m", "/m/canary.gguf",
            "-f", "/tmp/a.wav",
            "-l", "en",
            "-sl", "en",
            "-tl", "de",
            "-t", "16",
            "--no-auto-aligner",
            "--cache-dir", "/data/crispasr/cache",
            "--vad",
            "--vad-model", "/vad/silero.bin",
            "--vad-threshold", "0.5",
            "--vad-min-speech-duration-ms", "250",
            "--vad-min-silence-duration-ms", "100",
            "--vad-max-speech-duration-s", "30",
            "--vad-speech-pad-ms", "30",
            "--vad-samples-overlap", "0.1",
            "--max-len", "42",
            "--split-on-word",
            "--print-progress",
            "-osrt",
            "-of", "/tmp/out",
        }, Build(threads: 16, tuning: tuning, maxLineLength: 42, target: "de"));
    }

    // Tuning is passed through the same helper whisper-cli uses: an unset field emits nothing.
    [Fact]
    public void BuildArguments_PartialVadTuning_EmitsOnlyTheSetFlags()
    {
        var args = Build(tuning: new VadTuning(Threshold: 0.35f));
        var idx = IndexOf(args, "--vad-threshold");
        Assert.True(idx > IndexOf(args, "--vad-model"));
        Assert.Equal("0.35", args[idx + 1]);
        Assert.DoesNotContain("--vad-min-speech-duration-ms", args);
        Assert.DoesNotContain("--vad-samples-overlap", args);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void BuildArguments_NonPositiveMaxLen_EmitsNeitherFlag(int maxLen)
    {
        var args = Build(maxLineLength: maxLen);
        Assert.DoesNotContain("--max-len", args);
        Assert.DoesNotContain("--split-on-word", args);
    }

    [Fact]
    public void BuildArguments_MaxLen_IsAlwaysPairedWithSplitOnWord()
    {
        var args = Build(maxLineLength: 37);
        var idx = IndexOf(args, "--max-len");
        Assert.Equal("37", args[idx + 1]);
        Assert.Equal("--split-on-word", args[idx + 2]);
    }

    [Fact]
    public void BuildArguments_ZeroThreads_LeavesTheEngineDefault()
    {
        Assert.DoesNotContain("-t", Build(threads: 0));
    }

    // Canary ignores --prompt, and whisper-cli's language prompt must never leak into this command.
    [Fact]
    public void BuildArguments_NeverSendsAPrompt()
    {
        Assert.DoesNotContain("--prompt", Build(threads: 4, tuning: new VadTuning(0.5f), maxLineLength: 42));
        Assert.DoesNotContain("--translate", Build());
        Assert.DoesNotContain("--detect-language", Build());
    }

    [Theory]
    [InlineData("en", "nl", "nl")]
    [InlineData(" EN ", " LV ", "lv")]
    public void ValidateRequest_EnglishIntoACanaryTarget_ReturnsTheNormalizedTarget(string source, string target, string expected)
    {
        Assert.Equal(expected, CanaryProvider.ValidateRequest(source, target));
    }

    [Theory]
    [InlineData("fr", "es")]
    [InlineData("auto", "nl")]
    [InlineData(null, "nl")]
    [InlineData("en", "en")]
    [InlineData("en", "ja")]
    [InlineData("en", null)]
    public void ValidateRequest_AnythingElse_IsRefused(string? source, string? target)
    {
        Assert.Throws<NotSupportedException>(() => CanaryProvider.ValidateRequest(source, target));
    }

    [Fact]
    public async Task DetectLanguageAsync_IsNotSupported()
    {
        var provider = NewProvider("/nope/crispasr", "/nope/model.gguf", "/nope/vad.bin");
        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => provider.DetectLanguageAsync("/tmp/a.wav", CancellationToken.None));
        Assert.Contains("Whisper", ex.Message);
        Assert.Equal("Canary", provider.Name);
        Assert.True(provider.RequiresSpeechAlignmentOptIn);
    }

    [Fact]
    public async Task TranscribeAsync_RefusesANonEnglishSourceBeforeTouchingTheDisk()
    {
        var provider = NewProvider("/nope/crispasr", "/nope/model.gguf", "/nope/vad.bin");
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            provider.TranscribeAsync("/nope/a.wav", "fr", CancellationToken.None, translate: true, targetLanguage: "es"));
    }

    [Fact]
    public async Task TranscribeAsync_MissingFiles_FailWithAClearError()
    {
        var root = NewRoot("missing");
        try
        {
            var binary = Touch(root, "crispasr");
            var model = Touch(root, "canary.gguf");
            var vad = Touch(root, "silero.bin");
            var audio = Touch(root, "a.wav");

            var noBinary = await Assert.ThrowsAsync<FileNotFoundException>(() =>
                NewProvider(Path.Combine(root, "absent"), model, vad).TranscribeAsync(audio, "en", CancellationToken.None, true, "nl"));
            Assert.Contains("crispasr binary", noBinary.Message);

            var noModel = await Assert.ThrowsAsync<FileNotFoundException>(() =>
                NewProvider(binary, Path.Combine(root, "absent"), vad).TranscribeAsync(audio, "en", CancellationToken.None, true, "nl"));
            Assert.Contains("Canary model", noModel.Message);

            var noVad = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                NewProvider(binary, model, "").TranscribeAsync(audio, "en", CancellationToken.None, true, "nl"));
            Assert.Contains("VAD", noVad.Message);

            var noAudio = await Assert.ThrowsAsync<FileNotFoundException>(() =>
                NewProvider(binary, model, vad).TranscribeAsync(Path.Combine(root, "absent.wav"), "en", CancellationToken.None, true, "nl"));
            Assert.Contains("Audio file", noAudio.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // End to end through the shared process runner with a stand-in binary: the exact vector reaches
    // the process, the progress line is accepted, and the SRT at the -of prefix is read back.
    [Fact]
    public async Task TranscribeAsync_FakeCrispAsr_ReceivesTheVectorAndReturnsItsSrt()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var root = NewRoot("run");
        try
        {
            var argsFile = Path.Combine(root, "args.txt");
            var binary = Script(root,
                $"printf '%s\\n' \"$@\" > '{argsFile}'\n" +
                "for last; do :; done\n" +
                "echo 'crispasr: progress =  50% (1/2 slices)' >&2\n" +
                "printf '1\\n00:00:00,000 --> 00:00:01,000\\n Hallo\\n' > \"$last.srt\"\n");
            var model = Touch(root, "canary.gguf");
            var vad = Touch(root, "silero.bin");
            var audio = Touch(root, "a.wav");
            var cache = Path.Combine(root, "cache");

            var provider = new CanaryProvider(NullLogger.Instance, binary, model, 8, vad, new VadTuning(0.5f), 42, cache);
            var srt = await provider.TranscribeAsync(audio, "en", CancellationToken.None, translate: true, targetLanguage: "NL");

            Assert.Contains("Hallo", srt);
            Assert.True(Directory.Exists(cache));
            var passed = File.ReadAllLines(argsFile);
            var expectedPrefix = passed[^1];
            Assert.Equal(
                CanaryProvider.BuildArguments(model, audio, "nl", 8, vad, new VadTuning(0.5f), 42, cache, expectedPrefix),
                passed);
            Assert.False(File.Exists(expectedPrefix + ".srt"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // A crispasr that cannot start must never raise WhisperLaunchException: the dispatcher parks the
    // local whisper worker on that exception, and a broken Canary install must not stop transcription.
    [Theory]
    [InlineData(1)]
    [InlineData(127)]
    [InlineData(132)]
    public async Task TranscribeAsync_FailedExit_IsAnOrdinaryErrorNotALaunchFailure(int exitCode)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var root = NewRoot("fail");
        try
        {
            var binary = Script(root, $"echo 'error: no devices found' >&2\nexit {exitCode}\n");
            var provider = NewProvider(binary, Touch(root, "canary.gguf"), Touch(root, "silero.bin"));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                provider.TranscribeAsync(Touch(root, "a.wav"), "en", CancellationToken.None, true, "nl"));
            Assert.IsNotType<WhisperLaunchException>(ex);
            Assert.Null(WhisperLaunchException.Find(ex));
            Assert.Contains("crispasr", ex.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task TranscribeAsync_ExitZeroWithoutOutput_SurfacesTheEnginesComplaint()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        var root = NewRoot("empty");
        try
        {
            var binary = Script(root, "echo 'crispasr: error: failed to load model' >&2\nexit 0\n");
            var provider = NewProvider(binary, Touch(root, "canary.gguf"), Touch(root, "silero.bin"));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                provider.TranscribeAsync(Touch(root, "a.wav"), "en", CancellationToken.None, true, "nl"));
            Assert.Contains("failed to load model", ex.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DescribeExitFailure_NamesTheCauseAndTheFix()
    {
        var missing = CanaryProvider.DescribeExitFailure(127, "crispasr: error while loading shared libraries: libvulkan.so.1: cannot open shared object file");
        Assert.StartsWith("crispasr could not start: missing libvulkan.so.1.", missing);
        Assert.Contains("CPU variant", missing);

        Assert.StartsWith("crispasr could not start: missing a shared library.", CanaryProvider.DescribeExitFailure(127, null));
        Assert.Contains("crashed (exit 132)", CanaryProvider.DescribeExitFailure(132, ""));
        Assert.Contains("crashed (exit 134)", CanaryProvider.DescribeExitFailure(134, ""));
        Assert.Contains("crashed (exit 135)", CanaryProvider.DescribeExitFailure(135, ""));

        var other = CanaryProvider.DescribeExitFailure(1, new string('x', 2000) + "tail");
        Assert.StartsWith("crispasr failed with exit code 1. Error: ", other);
        Assert.EndsWith("tail", other);
        Assert.True(other.Length < 1100);
    }

    [Theory]
    [InlineData("error: unknown argument: --bogus", "crispasr exited without producing a subtitle file. It reported: error: unknown argument: --bogus")]
    [InlineData("loading...\ncrispasr: error: bad model\n", "crispasr exited without producing a subtitle file. It reported: crispasr: error: bad model")]
    [InlineData("all good", null)]
    [InlineData(null, null)]
    public void DescribeMissingOutput_UsesTheEnginesOwnErrorLine(string? stderr, string? expected)
    {
        Assert.Equal(expected, CanaryProvider.DescribeMissingOutput(stderr));
    }

    private static CanaryProvider NewProvider(string binary, string model, string vad)
        => new(NullLogger.Instance, binary, model, 0, vad, null, 0, Path.Combine(Path.GetTempPath(), "canary-cache-" + Guid.NewGuid().ToString("N")));

    private static string NewRoot(string name)
    {
        var root = Path.Combine(Path.GetTempPath(), "canary-" + name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string Touch(string root, string name)
    {
        var path = Path.Combine(root, name);
        File.WriteAllText(path, "x");
        return path;
    }

    private static string Script(string root, string body)
    {
        var path = Path.Combine(root, "fake-crispasr.sh");
        File.WriteAllText(path, "#!/bin/sh\n" + body);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
    }

    private static int IndexOf(IReadOnlyList<string> args, string value)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == value) return i;
        }
        return -1;
    }
}
