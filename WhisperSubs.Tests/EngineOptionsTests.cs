using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using WhisperSubs.Configuration;
using WhisperSubs.Controller;
using WhisperSubs.Controller.Workers;
using WhisperSubs.Providers;
using WhisperSubs.ScheduledTasks;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Every engine and detection behaviour added in 4.13-4.16 is a setting with a default that keeps what the
/// code did before, and a value out of range never reaches an engine.
/// </summary>
public class EngineOptionsTests
{
    [Fact]
    public void Defaults_KeepTheBehaviourBeforeTheSettings()
    {
        var c = new PluginConfiguration();
        Assert.False(c.DetectLanguageWithTranscriptionModel);
        Assert.Equal("", c.DetectionModelPath);
        Assert.Equal(DetectionDevice.Auto, c.DetectionDevice);
        Assert.Equal(0, c.DetectionThreadCount);
        Assert.Equal(0.3f, c.ForcedLanguageMinProbability);
        Assert.Equal(32, c.DetectionBatchSize);
        Assert.Equal(300, c.DetectionTimeoutSeconds);
        Assert.Equal("", c.VulkanDevice);
        Assert.Equal(1, c.MaxFullModelEngines);
        Assert.True(c.AllowDetectionBesideFullModel);
        Assert.Equal(UncoveredForcedLineAction.Whisper, c.UncoveredForcedLines);
        Assert.True(c.TranslateForcedLinesToEnglish);
        Assert.Equal(600, c.LocalQwen3WindowSeconds);
        Assert.True(c.CleanTempLeftoversAtStart);
        Assert.True(c.SuspendRemoteJobsDuringPlayback);
        var row = new WhisperWorker();
        Assert.Equal(-5, row.Priority);
        Assert.Equal(0, row.WindowSeconds);
        Assert.Empty(row.Languages);
    }

    [Theory]
    [InlineData(0.3f, 0.3f)]
    [InlineData(0f, 0f)]
    [InlineData(1f, 1f)]
    [InlineData(-0.1f, 0.3f)]
    [InlineData(1.5f, 0.3f)]
    [InlineData(float.NaN, 0.3f)]
    public void ForcedLanguageMinProbability_OutOfRangeIsTheDefault(float value, float expected)
        => Assert.Equal(expected, EngineOptions.ForcedLanguageMinProbability(value));

    [Theory]
    [InlineData(0, 32)]
    [InlineData(-3, 32)]
    [InlineData(1, 1)]
    [InlineData(64, 64)]
    [InlineData(500, 64)]
    public void DetectionBatchSize_IsOneTo64(int value, int expected)
        => Assert.Equal(expected, EngineOptions.DetectionBatchSize(value));

    [Theory]
    [InlineData(0, 300)]
    [InlineData(5, 30)]
    [InlineData(120, 120)]
    [InlineData(99999, 3600)]
    public void DetectionTimeout_Is30To3600Seconds(int value, int expectedSeconds)
        => Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), EngineOptions.DetectionTimeout(value));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 3)]
    [InlineData(99, 8)]
    public void MaxFullModelEngines_IsOneTo8(int value, int expected)
        => Assert.Equal(expected, EngineOptions.MaxFullModelEngines(value));

    [Theory]
    [InlineData(0, 600)]
    [InlineData(10, 60)]
    [InlineData(900, 900)]
    [InlineData(100000, 7200)]
    public void WindowSeconds_ZeroIsTheDefault(int value, int expected)
        => Assert.Equal(expected, EngineOptions.WindowSeconds(value));

    [Theory]
    [InlineData(-5, -5)]
    [InlineData(-50, -10)]
    [InlineData(50, 10)]
    public void AkouPriority_IsMinus10To10(int value, int expected)
        => Assert.Equal(expected, EngineOptions.AkouPriority(value));

    [Theory]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("0", "0")]
    [InlineData(" 0, 1 ", "0,1")]
    [InlineData("renderD128", "")]
    [InlineData("0;rm -rf", "")]
    public void VulkanDevice_OnlyDeviceIndexes(string? value, string expected)
        => Assert.Equal(expected, EngineOptions.VulkanDevice(value));

    [Fact]
    public void Languages_AreCleanIsoCodes()
        => Assert.Equal(new[] { "es", "en", "yue" },
            EngineOptions.Languages(new[] { " ES", "en", "es", "spanish", "e1", "yue", "" }));

    [Fact]
    public void DetectionSettings_FromTheConfiguration()
    {
        var s = DetectionSettings.From(new PluginConfiguration { DetectionDevice = DetectionDevice.Cpu, DetectionThreadCount = 6, DetectionTimeoutSeconds = 5 });
        Assert.Equal(new DetectionSettings(DetectionDevice.Cpu, 6, TimeSpan.FromSeconds(30)), s);
    }

    [Theory]
    [InlineData(true, "/m/ggml-small.bin", true, "")]                       // the transcription model wins
    [InlineData(false, "", true, "/d/ggml-base.bin")]                       // nothing custom: the small model
    [InlineData(false, "/m/ggml-small.bin", true, "/m/ggml-small.bin")]     // a custom model that exists
    [InlineData(false, "/m/missing.bin", false, "/d/ggml-base.bin")]        // missing: back to the small model
    public void DetectionModel_TranscriptionThenCustomThenSmall(bool useTranscription, string custom, bool customExists, string expected)
        => Assert.Equal(expected, SubtitleProviderFactory.ResolveDetectionModel(
            useTranscription, custom, "/d/ggml-base.bin", p => p == custom && customExists));

    [Theory]
    [InlineData(0.3f, 0.35f, true)]
    [InlineData(0.5f, 0.35f, false)]
    [InlineData(0f, 0.01f, true)]
    public void IsForeignDetection_UsesTheThreshold(float threshold, float probability, bool foreign)
        => Assert.Equal(foreign, SubtitleManager.IsForeignDetection("fr", probability, "es", threshold));

    [Fact]
    public async Task ForcedDetection_UsesTheBatchSizeAndThreshold()
    {
        var chunks = Enumerable.Range(0, 10).Select(i => (Start: i * 10.0, End: i * 10.0 + 5)).ToList();
        var batches = new List<int>();

        var r = await SubtitleManager.DetectForeignChunksAsync(
            chunks, "es",
            (i, _) => Task.FromResult($"/t/chunk_{i:D4}.wav"),
            (paths, _) =>
            {
                batches.Add(paths.Count);
                // Even chunks are French at 0.4, odd ones French at 0.6.
                return Task.FromResult(new BatchDetectionResult(paths
                    .Select(p => ((string, float)?)("fr", int.Parse(p.Substring(9, 4)) % 2 == 0 ? 0.4f : 0.6f))
                    .ToList()));
            },
            (_, _) => Task.FromResult(("es", 0.9f)),
            NullLogger.Instance, "item", CancellationToken.None,
            batchSize: 4, minProbability: 0.5f);

        Assert.Equal(new[] { 4, 4, 2 }, batches);
        Assert.Equal(5, r.ForeignChunks.Count);   // only the 0.6 ones clear 0.5
    }

    [Theory]
    [InlineData(true, "en", true)]
    [InlineData(false, "en", false)]
    [InlineData(true, "es", false)]
    public void TranslateForced_OnlyEnglishTitlesAndOnlyWhenOn(bool setting, string primary, bool expected)
        => Assert.Equal(expected, SubtitleManager.ShouldTranslateForced(setting, primary));

    [Theory]
    [InlineData(true, false, "gl", true)]     // Qwen3-ASR title, Galician line: left out when the setting skips
    [InlineData(true, false, "fr", false)]    // a language Qwen3-ASR covers
    [InlineData(true, true, "gl", false)]     // being translated into English: Whisper
    [InlineData(false, false, "gl", false)]   // a Whisper title
    public void UncoveredLine_SkippedOnlyOnQwenTitles(bool qwen, bool translate, string language, bool skipped)
        => Assert.Equal(skipped, SubtitleManager.SkipsUncoveredForcedLine(qwen, translate, language));

    [Fact]
    public void QwenTranscribes_HostAssistedRowsAndTheLocalQwenEngine()
    {
        var whisper = new WhisperProvider(NullLogger<WhisperProvider>.Instance, "/m/ggml-large-v3.bin");
        var remote = new RemoteWhisperProvider(NullLogger.Instance, "http://w:1", "best", dialect: WorkerDialect.Akou);
        Assert.True(SubtitleManager.QwenTranscribes(new HostAssistedProvider(remote, whisper)));
        Assert.False(SubtitleManager.QwenTranscribes(whisper));
        Assert.False(SubtitleManager.QwenTranscribes(remote));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void RemoteJobs_SuspendOnlyWhenTheSettingIsOn(bool suspendRemote, bool suspended)
    {
        Assert.Equal(suspended ? SubtitleGenerationTask.PlaybackPlan.Suspend : SubtitleGenerationTask.PlaybackPlan.RunThrough,
            SubtitleGenerationTask.PlanForPlayback(false, true, suspendRemote));
        // A local job is not affected by the setting.
        Assert.Equal(SubtitleGenerationTask.PlaybackPlan.WaitThenSuspend, SubtitleGenerationTask.PlanForPlayback(true, true, suspendRemote));
    }

    [Fact]
    public void AkouFields_CarryThePriorityAndTheLanguagesBound()
    {
        var fields = RemoteWhisperProvider.BuildAkouJobFields("best", "auto", 3, new[] { "es", "en" });
        Assert.Equal(new[] { ("model", "best"), ("languages[]", "es"), ("languages[]", "en"), ("priority", "3"), ("title", "whisper-subs") }, fields);

        // A named language needs no bound.
        var named = RemoteWhisperProvider.BuildAkouJobFields("best", "es", -5, new[] { "es", "en" });
        Assert.DoesNotContain(named, f => f.Name == "languages[]");
    }

    [Fact]
    public void Windows_TheLocalSettingAndTheRowSetting()
    {
        var whisper = new WhisperProvider(NullLogger<WhisperProvider>.Instance, "/m/ggml-large-v3.bin");
        var akou = new RemoteWhisperProvider(NullLogger.Instance, "http://w:1", "best", dialect: WorkerDialect.Akou,
            akou: new AkouJobOptions(-5, 1200, Array.Empty<string>()));
        Assert.Equal(1200, SubtitleManager.WindowSecondsFor(new HostAssistedProvider(akou, whisper), 900));
        var qwen = new Qwen3Provider(NullLogger.Instance, "/b/crispasr", "/m/qwen.gguf", 0, "", null, 0, "/c");
        Assert.Equal(900, SubtitleManager.WindowSecondsFor(new EngineSwitchProvider(whisper, () => qwen), 900));
    }

    [Fact]
    public void Signature_UnchangedForDefaultRows_ChangedByAkouSettings()
    {
        WhisperWorker Row() => new() { Id = "a", ApiUrl = "http://a:1", Dialect = "akou" };
        string Sig(WhisperWorker w) => SubtitleQueueService.ComputeWorkersSignature(new PluginConfiguration { Workers = new() { w } });

        var baseline = Sig(Row());
        Assert.DoesNotContain("p=", baseline);
        var changed = Row(); changed.Priority = 0;
        Assert.NotEqual(baseline, Sig(changed));
        var window = Row(); window.WindowSeconds = 900;
        Assert.NotEqual(baseline, Sig(window));
        var languages = Row(); languages.Languages = new() { "es" };
        Assert.NotEqual(baseline, Sig(languages));
    }
}

/// <summary>The GPUs the settings page lists, and the SR-IOV warning that would have saved a wedged iGPU.</summary>
public sealed class GpuInventoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gpuinv_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Scan_FindsTheVirtualFunctions()
    {
        if (OperatingSystem.IsWindows()) return;   // symbolic links
        var dev = Node("renderD128", "0000:00:02.0", vf: false);
        Node("renderD129", "0000:00:02.1", vf: true);
        Node("renderD130", "0000:00:02.2", vf: true);

        var nodes = GpuInventory.Scan(dev, Path.Combine(_root, "sys"));

        Assert.Equal(new[] { "renderD128", "renderD129", "renderD130" }, nodes.Select(n => n.Node));
        Assert.Equal(new[] { "0000:00:02.0", "0000:00:02.1", "0000:00:02.2" }, nodes.Select(n => n.PciAddress));
        Assert.Equal(new[] { false, true, true }, nodes.Select(n => n.VirtualFunction));
        Assert.All(nodes, n => Assert.Equal("i915", n.Driver));
        var warning = GpuInventory.Warning(nodes)!;
        Assert.Contains("renderD129, renderD130", warning);
        Assert.Contains("renderD128 and its card node", warning);
    }

    [Fact]
    public void Warning_SeveralGpusOrNone()
    {
        Assert.Null(GpuInventory.Warning(new[] { new GpuNode("renderD128", "0000:00:02.0", false, "i915") }));
        Assert.Contains("2 GPU render nodes", GpuInventory.Warning(new[]
        {
            new GpuNode("renderD128", "0000:00:02.0", false, "i915"),
            new GpuNode("renderD129", "0000:03:00.0", false, "amdgpu"),
        })!);
        Assert.Empty(GpuInventory.Scan(Path.Combine(_root, "nope"), Path.Combine(_root, "sys")));
    }

    // Lays out /dev/dri/<node> and /sys/class/drm/<node>/device -> ../pci/<address> as the kernel does.
    private string Node(string node, string address, bool vf)
    {
        var dev = Path.Combine(_root, "dev");
        Directory.CreateDirectory(dev);
        File.WriteAllText(Path.Combine(dev, node), "");
        var pci = Path.Combine(_root, "pci", address);
        Directory.CreateDirectory(pci);
        var driver = Path.Combine(_root, "drivers", "i915");
        Directory.CreateDirectory(driver);
        if (!File.Exists(Path.Combine(pci, "driver"))) Directory.CreateSymbolicLink(Path.Combine(pci, "driver"), driver);
        if (vf) Directory.CreateSymbolicLink(Path.Combine(pci, "physfn"), Path.Combine(_root, "pci", "0000:00:02.0"));
        var sysNode = Path.Combine(_root, "sys", node);
        Directory.CreateDirectory(sysNode);
        Directory.CreateSymbolicLink(Path.Combine(sysNode, "device"), pci);
        return dev;
    }
}
