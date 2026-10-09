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
/// Language detection runs on the GPU. whisper.cpp v1.8.4 on Vulkan (Intel UHD 770) answered every chunk
/// "nl (p = 0.010000)", so before the first detection the GPU has to name an English clip as English; if it
/// cannot, detection runs with --no-gpu and the answers stay right.
/// </summary>
[Collection(nameof(HostEngineGates))]
public sealed class GpuDetectionCheckTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gpuselftest_" + Guid.NewGuid().ToString("N"));
    private readonly string _log;
    private readonly string _model;
    private readonly string[] _chunks;

    public GpuDetectionCheckTests()
    {
        Directory.CreateDirectory(_dir);
        _log = Path.Combine(_dir, "runs.log");
        _model = Path.Combine(_dir, "ggml-large-v3.bin");
        File.WriteAllText(_model, "model");
        _chunks = new[] { "chunk_0000.wav", "chunk_0001.wav" }.Select(n => Path.Combine(_dir, n)).ToArray();
        foreach (var c in _chunks) File.WriteAllBytes(c, WhisperSubs.Controller.Workers.SyntheticAudio.SilentWav16kMono(500));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData("whisper_full_with_state: auto-detected language: en (p = 0.977103)", true)]
    [InlineData("whisper_full_with_state: auto-detected language: nl (p = 0.010000)", false)]   // v1.8.4 Vulkan
    [InlineData("whisper_full_with_state: auto-detected language: en (p = 0.010000)", false)]   // flat answer that lands on en
    [InlineData("whisper_full_with_state: auto-detected language: es (p = 0.990000)", false)]
    [InlineData("whisper_full_with_state: auto-detected language: english (p = 0.950)", true)]
    [InlineData("error: failed to initialize whisper context", false)]
    [InlineData("", false)]
    public void Passed_OnlyForConfidentEnglish(string output, bool expected)
        => Assert.Equal(expected, GpuDetectionCheck.Passed(output));

    [Fact]
    public void TheClipShipsInThePlugin()
    {
        var path = GpuDetectionCheck.WriteClip();
        try
        {
            Assert.StartsWith("whispersubs_", Path.GetFileName(path));
            var bytes = File.ReadAllBytes(path);
            Assert.True(bytes.Length > 100_000);
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task BrokenGpu_FallsBackToTheCpu_AndAnswersStayRight()
    {
        if (OperatingSystem.IsWindows()) return;   // the stand-in binary is a bash script

        var whisper = Provider(Executable(brokenGpu: true));

        var batch = await whisper.DetectLanguagesAsync(_chunks, CancellationToken.None);
        var single = await whisper.DetectLanguageAsync(_chunks[0], CancellationToken.None);

        Assert.All(batch.Results, r => Assert.Equal("es", r!.Value.Language));
        Assert.Equal("es", single.Language);
        var runs = Runs();
        Assert.Equal(3, runs.Count);
        Assert.True(runs[0].Check && runs[0].Gpu);        // the self-check, once, on the GPU
        Assert.All(runs.Skip(1), r => Assert.False(r.Check || r.Gpu));
        Assert.False(File.Exists(runs[0].Files[0]));     // the clip is removed after the check
    }

    [Fact]
    public async Task HealthyGpu_DetectsOnTheGpu()
    {
        if (OperatingSystem.IsWindows()) return;

        var whisper = Provider(Executable(brokenGpu: false));

        var batch = await whisper.DetectLanguagesAsync(_chunks, CancellationToken.None);
        await whisper.DetectLanguagesAsync(_chunks, CancellationToken.None);

        Assert.All(batch.Results, r => Assert.Equal("es", r!.Value.Language));
        var runs = Runs();
        Assert.Equal(3, runs.Count);
        Assert.Single(runs, r => r.Check);
        Assert.All(runs, r => Assert.True(r.Gpu));
    }

    [Fact]
    public async Task AReplacedBinary_IsCheckedAgain()
    {
        if (OperatingSystem.IsWindows()) return;

        var exe = Executable(brokenGpu: true);
        var whisper = Provider(exe);
        await whisper.DetectLanguagesAsync(_chunks, CancellationToken.None);

        // A fixed whisper-cli dropped in place of the broken one.
        File.WriteAllText(exe, Script(brokenGpu: false));
        File.SetLastWriteTimeUtc(exe, DateTime.UtcNow.AddMinutes(5));
        await whisper.DetectLanguagesAsync(_chunks, CancellationToken.None);

        var runs = Runs();
        Assert.Equal(4, runs.Count);
        Assert.Equal(2, runs.Count(r => r.Check));
        Assert.True(runs[3].Gpu);
    }

    [Fact]
    public async Task AReplacedModel_IsCheckedAgain()
    {
        if (OperatingSystem.IsWindows()) return;

        var whisper = Provider(Executable(brokenGpu: false));
        await whisper.DetectLanguagesAsync(_chunks, CancellationToken.None);

        File.SetLastWriteTimeUtc(_model, DateTime.UtcNow.AddMinutes(5));
        await whisper.DetectLanguagesAsync(_chunks, CancellationToken.None);

        Assert.Equal(2, Runs().Count(r => r.Check));
    }

    private WhisperProvider Provider(string exe)
        => new(NullLogger<WhisperProvider>.Instance, _model, exe, detectionModelPath: "");

    private List<(bool Gpu, bool Check, string[] Files)> Runs()
        => File.ReadAllLines(_log)
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(p => (p[0] == "1", p.Skip(1).Any(f => f.Contains("whispersubs_gpucheck_")), p.Skip(1).ToArray()))
            .ToList();

    // Answers like whisper-cli --detect-language: a "processing" line and a detected language per file.
    // A broken GPU says nl at p = 0.010 for everything; otherwise the check clip is English and chunks Spanish.
    private string Script(bool brokenGpu) => $$"""
        #!/bin/bash
        gpu=1; files=()
        while [ $# -gt 0 ]; do
          case "$1" in --no-gpu) gpu=0;; -f) files+=("$2"); shift;; esac
          shift
        done
        echo "$gpu ${files[*]}" >> "{{_log}}"
        for f in "${files[@]}"; do
          echo "main: processing '$f' (8000 samples, 0.5 sec), 4 threads, 1 processors, lang = auto, task = transcribe ..." >&2
          if [ $gpu = 1 ] && [ {{(brokenGpu ? 1 : 0)}} = 1 ]; then
            echo "whisper_full_with_state: auto-detected language: nl (p = 0.010000)" >&2
          else
            case "$f" in *whispersubs_gpucheck_*) l=en;; *) l=es;; esac
            echo "whisper_full_with_state: auto-detected language: $l (p = 0.970000)" >&2
          fi
        done
        """.Replace("\r\n", "\n");

    private string Executable(bool brokenGpu)
    {
        var path = Path.Combine(_dir, "whisper-cli");
        File.WriteAllText(path, Script(brokenGpu));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
    }
}
