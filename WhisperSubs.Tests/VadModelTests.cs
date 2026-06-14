using System.Diagnostics;
using System.IO;
using WhisperSubs.Providers;
using WhisperSubs.Setup;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Covers the native Silero VAD plumbing (issue #78): the model catalog entry, the
/// setup-service path resolution, and the WhisperProvider arg-denylist that blocks
/// callers from overriding the plugin-managed --vad/--vad-model flags.
/// </summary>
public class VadModelTests
{
    private static WhisperSetupService CreateService(out string dataPath)
    {
        dataPath = Path.Combine(Path.GetTempPath(), "whispersubs-vad-" + Guid.NewGuid().ToString("N"));
        var logger = new NullLogger<WhisperSetupService>();
        return new WhisperSetupService(logger, dataPath);
    }

    [Fact]
    public void ModelCatalog_VadConstants_AreWellFormed()
    {
        Assert.EndsWith(".bin", ModelCatalog.VadModelFileName);
        Assert.Contains("whisper-vad", ModelCatalog.VadModelUrl);
        Assert.EndsWith(ModelCatalog.VadModelFileName, ModelCatalog.VadModelUrl);
        Assert.True(ModelCatalog.VadModelSizeBytes > 0);
    }

    [Fact]
    public void VadDirectory_And_VadModelPath_AreUnderDataPath()
    {
        var service = CreateService(out var dataPath);

        var expectedVadDir = Path.Combine(dataPath, "whisper", "vad");
        Assert.Equal(expectedVadDir, service.VadDirectory);

        Assert.EndsWith(ModelCatalog.VadModelFileName, service.VadModelPath);
        Assert.Equal(service.VadDirectory, Path.GetDirectoryName(service.VadModelPath));
        Assert.Equal(Path.Combine(service.VadDirectory, ModelCatalog.VadModelFileName), service.VadModelPath);
    }

    [Fact]
    public void ResolveVadModelPath_ConfiguredPathExists_ReturnsIt()
    {
        var service = CreateService(out _);
        var tempFile = Path.Combine(Path.GetTempPath(), "whispersubs-vad-cfg-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            File.WriteAllText(tempFile, "vad");

            Assert.Equal(tempFile, service.ResolveVadModelPath(tempFile));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ResolveVadModelPath_ConfiguredPathMissing_FallsBackToDefault()
    {
        var service = CreateService(out _);
        try
        {
            Directory.CreateDirectory(service.VadDirectory);
            File.WriteAllText(service.VadModelPath, "vad");

            var missingConfigured = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid().ToString("N") + ".bin");

            Assert.Equal(service.VadModelPath, service.ResolveVadModelPath(missingConfigured));
        }
        finally
        {
            if (Directory.Exists(service.VadDirectory)) Directory.Delete(service.VadDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveVadModelPath_NothingExists_ReturnsNull(string configured)
    {
        // Fresh temp dataPath => no default VAD file on disk, and an empty/whitespace
        // configured path must not be treated as a real path.
        var service = CreateService(out _);

        Assert.Null(service.ResolveVadModelPath(configured));
    }

    [Fact]
    public void ResolveVadModelPath_NullConfigured_ReturnsNullWhenNoDefault()
    {
        var service = CreateService(out _);

        Assert.Null(service.ResolveVadModelPath(null));
    }

    // ── WhisperProvider denylist: plugin-managed VAD flags can't be overridden ──

    private static WhisperProvider CreateProvider(string customArgs)
    {
        var logger = NullLoggerFactory.Instance.CreateLogger<WhisperProvider>();
        return new WhisperProvider(logger, "/tmp/m.bin", "", 0, customArgs);
    }

    private static ProcessStartInfo CreateStartInfo()
    {
        return new ProcessStartInfo { FileName = "whisper-cli", UseShellExecute = false };
    }

    [Fact]
    public void AppendCustomArgs_VadFlagAndModel_FullyStripped()
    {
        var provider = CreateProvider("--vad --vad-model /evil.bin");
        var si = CreateStartInfo();
        provider.AppendCustomArgs(si);
        Assert.Empty(si.ArgumentList);
    }

    [Fact]
    public void AppendCustomArgs_VadShortFlag_Stripped()
    {
        var provider = CreateProvider("-v");
        var si = CreateStartInfo();
        provider.AppendCustomArgs(si);
        Assert.Empty(si.ArgumentList);
    }

    [Fact]
    public void AppendCustomArgs_VadModelShortFlagWithValue_BothStripped()
    {
        var provider = CreateProvider("-vm /x");
        var si = CreateStartInfo();
        provider.AppendCustomArgs(si);
        Assert.Empty(si.ArgumentList);
    }

    [Theory]
    [InlineData("--vad-threshold 0.9")]
    [InlineData("-vt 0.9")]
    [InlineData("--vad-min-speech-duration-ms 250")]
    [InlineData("-vspd 250")]
    [InlineData("--vad-speech-pad-ms 30")]
    [InlineData("-vp 30")]
    [InlineData("--vad-samples-overlap 0.1")]
    public void AppendCustomArgs_VadTuningFlags_Stripped(string customArgs)
    {
        // VAD is fully plugin-managed; tuning sub-flags must not pass through either.
        var provider = CreateProvider(customArgs);
        var si = CreateStartInfo();
        provider.AppendCustomArgs(si);
        Assert.Empty(si.ArgumentList);
    }

    // ── UsesVad: single source of truth for "VAD already applied" ──

    [Fact]
    public void WhisperProvider_UsesVad_FalseWhenNoModelPath()
    {
        var logger = NullLoggerFactory.Instance.CreateLogger<WhisperProvider>();
        var provider = new WhisperProvider(logger, "/tmp/m.bin", "", 0, "", vadModelPath: "");
        Assert.False(provider.UsesVad);
    }

    [Fact]
    public void WhisperProvider_UsesVad_FalseWhenModelPathMissingOnDisk()
    {
        var logger = NullLoggerFactory.Instance.CreateLogger<WhisperProvider>();
        var provider = new WhisperProvider(logger, "/tmp/m.bin", "", 0, "",
            vadModelPath: Path.Combine(Path.GetTempPath(), "nonexistent-" + Guid.NewGuid().ToString("N") + ".bin"));
        Assert.False(provider.UsesVad);
    }

    [Fact]
    public void WhisperProvider_UsesVad_TrueWhenModelPathExists()
    {
        var modelPath = Path.Combine(Path.GetTempPath(), "vad-present-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllText(modelPath, "x");
        try
        {
            var logger = NullLoggerFactory.Instance.CreateLogger<WhisperProvider>();
            var provider = new WhisperProvider(logger, "/tmp/m.bin", "", 0, "", vadModelPath: modelPath);
            Assert.True(provider.UsesVad);
        }
        finally
        {
            File.Delete(modelPath);
        }
    }
}
