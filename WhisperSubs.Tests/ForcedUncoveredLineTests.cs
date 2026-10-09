using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using WhisperSubs.Controller;
using WhisperSubs.Providers;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// With Whisper large-v3 detecting languages, a forced line it names Galician or Catalan is most likely
/// real, so on a Qwen3-ASR title the line goes to this server's Whisper (Qwen3-ASR does not cover those
/// languages) instead of being left out, and it waits for its turn on the full-model gate.
/// </summary>
[Collection(nameof(HostEngineGates))]
public sealed class ForcedUncoveredLineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "forcedline_" + Guid.NewGuid().ToString("N"));
    private readonly string _calls;
    private readonly string _whisper;
    private readonly string _model;
    private readonly string _audio;

    public ForcedUncoveredLineTests()
    {
        Directory.CreateDirectory(_dir);
        _calls = Path.Combine(_dir, "calls.log");
        _model = Path.Combine(_dir, "ggml-large-v3.bin");
        File.WriteAllText(_model, "model");
        _audio = Path.Combine(_dir, "foreign_10_14.wav");
        File.WriteAllBytes(_audio, Controller.Workers.SyntheticAudio.SilentWav16kMono(500));
        // A stand-in whisper-cli: records its arguments and writes an SRT at -of's prefix.
        _whisper = Path.Combine(_dir, "whisper-cli");
        File.WriteAllText(_whisper, $$"""
            #!/bin/bash
            echo "$@" >> "{{_calls}}"
            prefix=""
            while [ $# -gt 0 ]; do [ "$1" = "-of" ] && prefix="$2"; shift; done
            printf '1\n00:00:00,000 --> 00:00:02,000\nBos días.\n' > "$prefix.srt"
            """.Replace("\r\n", "\n"));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(_whisper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    // With a VAD model present, so a run that re-applies VAD to the already-trimmed line would show --vad.
    private WhisperProvider Whisper() => new(NullLogger<WhisperProvider>.Instance, _model, _whisper, vadModelPath: _model);

    private ISubtitleProvider LocalQwen3Title()
    {
        var qwen = new Qwen3Provider(NullLogger.Instance, "/nope/crispasr", "/nope/qwen3.gguf", 0, "", null, 0, _dir);
        return new EngineSwitchProvider(Whisper(), () => qwen);
    }

    private ISubtitleProvider AkouRowTitle()
        => new HostAssistedProvider(new RemoteWhisperProvider(NullLogger.Instance, "http://akou:8476", "best", dialect: "akou"), Whisper());

    [Theory]
    [InlineData("local")]
    [InlineData("akou")]
    public async Task UncoveredLanguageLine_OnAQwen3Title_IsTranscribedByLargeV3(string title)
    {
        if (OperatingSystem.IsWindows()) return;   // the stand-in binary is a bash script
        var provider = title == "local" ? LocalQwen3Title() : AkouRowTitle();

        var srt = await SubtitleManager.TranscribeForcedLineAsync(provider, _audio, "gl", translateForced: false, CancellationToken.None);

        Assert.Contains("Bos días.", srt);
        var call = File.ReadAllLines(_calls).Single();
        Assert.Contains($"-m {_model}", call);
        Assert.Contains("-l gl", call);
        Assert.DoesNotContain("--vad", call);
    }

    [Fact]
    public async Task UncoveredLanguageLine_WaitsForTheFullModelGate()
    {
        if (OperatingSystem.IsWindows()) return;

        await HostEngineGates.Model.WaitAsync();
        Task<string> line;
        try
        {
            line = SubtitleManager.TranscribeForcedLineAsync(LocalQwen3Title(), _audio, "ca", translateForced: false, CancellationToken.None);
            await Task.Delay(700);
            Assert.False(File.Exists(_calls));   // nothing ran while another full model held the gate
        }
        finally
        {
            HostEngineGates.Model.Release();
        }

        Assert.Contains("Bos días.", await line);
        Assert.Contains("-l ca", File.ReadAllLines(_calls).Single());
    }
}

public class QwenCoversTests
{
    [Theory]
    [InlineData("es", true)]
    [InlineData("gl", false)]
    [InlineData("ca", false)]
    [InlineData("auto", true)]
    [InlineData(null, true)]
    public void QwenCovers(string? language, bool covered)
        => Assert.Equal(covered, SubtitleManager.QwenCovers(language));
}
