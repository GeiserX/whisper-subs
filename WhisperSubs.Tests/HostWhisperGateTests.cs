using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using WhisperSubs.Providers;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Every title in flight can need an engine on this server: language detection for a Qwen3-ASR or akou
/// row, a foreign line to translate, a language Qwen3-ASR does not cover, the local Qwen3-ASR itself. Each
/// pool row has its own providers. One whisper-cli with large-v3 on Vulkan held about 6 GB of the container
/// and five titles in flight ran five at once against a 16 GB limit: Jellyfin was OOM-killed 21 times in
/// one night. However many titles and rows ask, one full-model engine process runs on this server at a
/// time, and one detection run on the small model beside it.
/// </summary>
[Collection(nameof(HostEngineGates))]
public sealed class HostWhisperGateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "enginegate_" + Guid.NewGuid().ToString("N"));
    private readonly string _log;
    private readonly string _whisper;
    private readonly string _crispasr;
    private readonly string _largeModel;
    private readonly string _baseModel;
    private readonly string _qwenModel;
    private readonly string _audio;

    public HostWhisperGateTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "running", "full"));
        Directory.CreateDirectory(Path.Combine(_dir, "running", "detect"));
        _log = Path.Combine(_dir, "peaks.log");
        _largeModel = Touch("ggml-large-v3.bin");
        _baseModel = Touch("ggml-base.bin");
        _qwenModel = Touch("qwen3-asr-1.7b-q8_0.gguf");
        _audio = Path.Combine(_dir, "chunk.wav");
        File.WriteAllBytes(_audio, Controller.Workers.SyntheticAudio.SilentWav16kMono(500));
        // A stand-in for whisper-cli and crispasr: notes how many full-model and how many detection-model
        // processes run at that moment, works for 300 ms, then answers like the real ones (an SRT at -of's
        // prefix, a detected language on stderr).
        var script = $$"""
            #!/bin/bash
            [ "$1" = "--version" ] && { echo "whisper.cpp version: 1.9.5"; exit 0; }
            model=""; prefix=""
            while [ $# -gt 0 ]; do
              case "$1" in -m) model="$2";; -of) prefix="$2";; esac
              shift
            done
            kind=full; case "$model" in *ggml-base.bin) kind=detect;; esac
            touch "{{_dir}}/running/$kind/$$"
            echo "full $(ls "{{_dir}}/running/full" | wc -l) detect $(ls "{{_dir}}/running/detect" | wc -l)" >> "{{_log}}"
            sleep 0.3
            if [ -n "$prefix" ]; then printf '1\n00:00:00,000 --> 00:00:01,000\nHello.\n' > "$prefix.srt"; fi
            echo "whisper_full_with_state: auto-detected language: es (p = 0.910)" >&2
            rm -f "{{_dir}}/running/$kind/$$"
            """.Replace("\r\n", "\n");
        _whisper = Executable("whisper-cli", script);
        _crispasr = Executable("crispasr", script);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task FiveTitlesOnFiveRows_RunOneWhisperCliAtATime()
    {
        if (OperatingSystem.IsWindows()) return;   // the stand-in binaries are bash scripts

        // One provider per row, as the pool builds them, and no small detection model: detection runs on
        // large-v3 like everything else.
        var rows = Enumerable.Range(0, 5).Select(_ => Whisper(detectionModel: "")).ToList();

        await Task.WhenAll(rows.SelectMany(TitleWork));

        // Three runs per title, plus the one GPU self-check before the first detection with that model.
        var lines = Peaks();
        Assert.Equal(16, lines.Count);
        Assert.Equal(1, lines.Max(p => p.Full + p.Detect));
    }

    [Fact]
    public async Task WithTheSmallDetectionModel_OneFullModelAndOneDetectionAtMost()
    {
        if (OperatingSystem.IsWindows()) return;

        var rows = Enumerable.Range(0, 5).Select(_ => Whisper(detectionModel: _baseModel)).ToList();

        await Task.WhenAll(rows.SelectMany(TitleWork));

        // Three runs per title, plus the one GPU self-check before the first detection with that model.
        var lines = Peaks();
        Assert.Equal(16, lines.Count);
        Assert.Equal(1, lines.Max(p => p.Full));
        Assert.Equal(1, lines.Max(p => p.Detect));
    }

    [Fact]
    public async Task TwoFullModelEnginesAllowed_TwoRunTogether_NeverThree()
    {
        if (OperatingSystem.IsWindows()) return;

        HostEngineGates.Apply(new Configuration.PluginConfiguration { MaxFullModelEngines = 2 });
        try
        {
            var rows = Enumerable.Range(0, 5).Select(_ => Whisper(detectionModel: "")).ToList();
            await Task.WhenAll(rows.SelectMany(TitleWork));
        }
        finally
        {
            HostEngineGates.Reset();
        }

        Assert.Equal(2, Peaks().Max(p => p.Full + p.Detect));
    }

    [Fact]
    public async Task DetectionNotBesideFullModels_SmallModelQueuesWithThem()
    {
        if (OperatingSystem.IsWindows()) return;

        HostEngineGates.Apply(new Configuration.PluginConfiguration { AllowDetectionBesideFullModel = false });
        try
        {
            Assert.Same(HostEngineGates.Model, WhisperProvider.GateForDetection(_baseModel, _largeModel));
            var rows = Enumerable.Range(0, 5).Select(_ => Whisper(detectionModel: _baseModel)).ToList();
            await Task.WhenAll(rows.SelectMany(TitleWork));
        }
        finally
        {
            HostEngineGates.Reset();
        }

        Assert.Equal(1, Peaks().Max(p => p.Full + p.Detect));
    }

    [Fact]
    public async Task LoweringTheLimit_LetsHoldersFinish_AndAdmitsNobodyUntilUnderIt()
    {
        var gate = new EngineGate(2);
        await gate.WaitAsync();
        await gate.WaitAsync();
        gate.Limit = 1;
        var third = gate.WaitAsync();
        gate.Release();
        Assert.False(third.IsCompleted);        // one still holds, and the limit is now 1
        gate.Release();
        await third.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, gate.Held);
        gate.Limit = 3;
        Assert.True(await gate.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Throws<System.Threading.SemaphoreFullException>(() => { gate.Release(); gate.Release(); gate.Release(); });
    }

    [Fact]
    public async Task LocalQwen3_QueuesWithWhisper()
    {
        if (OperatingSystem.IsWindows()) return;

        var qwen = new Qwen3Provider(NullLogger.Instance, _crispasr, _qwenModel, 0, "", null, 0, Path.Combine(_dir, "cache"));
        var whisper = Whisper(detectionModel: _baseModel);

        await Task.WhenAll(
            qwen.TranscribeAsync(_audio, "es", CancellationToken.None, translate: false, targetLanguage: null, applyVad: false),
            qwen.TranscribeAsync(_audio, "es", CancellationToken.None, translate: false, targetLanguage: null, applyVad: false),
            whisper.TranscribeAsync(_audio, "nn", CancellationToken.None, translate: false, applyVad: false),
            whisper.TranscribeAsync(_audio, "es", CancellationToken.None, translate: true, applyVad: false));

        Assert.Equal(1, Peaks().Max(p => p.Full));
    }

    [Fact]
    public async Task AWaitingRequest_CanBeCancelled_AndNoTurnIsLeaked()
    {
        await HostEngineGates.Model.WaitAsync();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => Whisper(detectionModel: "").DetectLanguageAsync(_audio, cts.Token));
        }
        finally
        {
            HostEngineGates.Model.Release();
        }

        // Other test classes run engines through the same gates in parallel, so "free right now" is not
        // the check: the turn the cancelled call never got must still come round.
        Assert.True(await HostEngineGates.Model.WaitAsync(TimeSpan.FromSeconds(30)));
        HostEngineGates.Model.Release();
        Assert.True(await HostEngineGates.Detection.WaitAsync(TimeSpan.FromSeconds(30)));
        HostEngineGates.Detection.Release();
    }

    [Theory]
    [InlineData("/m/ggml-large-v3.bin", "Model")]
    [InlineData("/m/ggml-base.bin", "Detection")]
    [InlineData("/m/./ggml-large-v3.bin", "Model")]       // another spelling of the transcription model
    public void DetectionQueuesByTheModelItRuns(string detectionModel, string gate)
        => Assert.Same(
            gate == "Model" ? HostEngineGates.Model : HostEngineGates.Detection,
            WhisperProvider.GateForDetection(detectionModel, "/m/ggml-large-v3.bin"));

    private IEnumerable<Task> TitleWork(WhisperProvider row) => new Task[]
    {
        row.TranscribeAsync(_audio, "es", CancellationToken.None, translate: true, applyVad: false),
        row.DetectLanguageAsync(_audio, CancellationToken.None),
        row.DetectLanguagesAsync(new[] { _audio }, CancellationToken.None),
    };

    private WhisperProvider Whisper(string detectionModel)
        => new(NullLogger<WhisperProvider>.Instance, _largeModel, _whisper, detectionModelPath: detectionModel);

    private List<(int Full, int Detect)> Peaks()
        => File.ReadAllLines(_log)
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(p => (int.Parse(p[1]), int.Parse(p[3])))
            .ToList();

    private string Touch(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "model");
        return path;
    }

    private string Executable(string name, string script)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, script);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
    }
}

public class DetectionModelChoiceTests
{
    [Theory]
    [InlineData("/m/ggml-base.bin", 16, 4)]          // the small model: 4 threads, as before
    [InlineData("/m/ggml-large-v3.bin", 16, 16)]     // the transcription model: the configured threads
    [InlineData("/m/ggml-large-v3.bin", 0, 4)]       // nothing configured: 4
    public void DetectionThreads(string detectionModel, int configured, int expected)
        => Assert.Equal(expected, WhisperProvider.DetectionThreads(detectionModel, "/m/ggml-large-v3.bin", configured));

    [Fact]
    public void BuildDetectionArgs_CarriesTheThreadCount()
        => Assert.Equal(
            new[] { "-m", "m.bin", "-f", "a.wav", "-l", "auto", "-t", "16", "--detect-language" },
            WhisperProvider.BuildDetectionArgs("m.bin", new[] { "a.wav" }, 16));

    [Fact]
    public void ChoosingTheTranscriptionModel_DetectsWithIt()
        // With the setting on, the factory hands over no small model, and detection falls back to the
        // transcription model, which queues on the full-model gate.
        => Assert.Equal("/m/ggml-large-v3.bin", WhisperProvider.ChooseDetectionModel("/m/ggml-large-v3.bin", "", false));
}
