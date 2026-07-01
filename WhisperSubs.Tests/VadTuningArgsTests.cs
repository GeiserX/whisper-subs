using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using WhisperSubs.Configuration;
using WhisperSubs.Providers;
using WhisperSubs.Setup;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Covers the VAD tuning flag emission (AppendVadTuning, BuildTranscribeArguments with tuning)
/// and the managed-path helpers (IsManagedVadPath, VadModelPathFor, version-aware
/// ResolveVadModelPath) introduced in issue #105.
/// </summary>
public class VadTuningArgsTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    private static WhisperSetupService CreateService(out string dataPath)
    {
        dataPath = Path.Combine(Path.GetTempPath(), "whispersubs-tuning-" + Guid.NewGuid().ToString("N"));
        var logger = new NullLogger<WhisperSetupService>();
        return new WhisperSetupService(logger, dataPath);
    }

    private static IReadOnlyList<string> BuildWithTuning(
        string? vadModelPath,
        VadTuning? tuning,
        string modelPath = "/m/model.bin",
        string audioPath = "/tmp/a.wav",
        string language = "es",
        string outputPrefix = "/tmp/out")
    {
        return WhisperProvider.BuildTranscribeArguments(
            modelPath, audioPath, language, 0, false, vadModelPath, outputPrefix, null, tuning);
    }

    private static int IndexOf(IReadOnlyList<string> args, string value)
    {
        for (var i = 0; i < args.Count; i++)
            if (args[i] == value) return i;
        return -1;
    }

    // ── VadTuning.Unset emits nothing ─────────────────────────────────────────

    [Fact]
    public void AppendVadTuning_Unset_EmitsNothing()
    {
        var args = new List<string>();
        WhisperProvider.AppendVadTuning(args, VadTuning.Unset);
        Assert.Empty(args);
    }

    // ── Individual fields ─────────────────────────────────────────────────────

    [Fact]
    public void AppendVadTuning_Threshold_EmitsFlagAndValue()
    {
        var args = new List<string>();
        WhisperProvider.AppendVadTuning(args, new VadTuning(Threshold: 0.4f));
        Assert.Equal(new[] { "--vad-threshold", "0.4" }, args);
    }

    /// <summary>
    /// Proves locale-independence: run under a comma-decimal culture and verify the emitted
    /// value is still "0.4" (InvariantCulture dot), never "0,4" (locale comma).
    /// </summary>
    [Fact]
    public void AppendVadTuning_Threshold_UsesInvariantCulture_NotLocaleComma()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("es-ES"); // uses comma decimal separator
            var args = new List<string>();
            WhisperProvider.AppendVadTuning(args, new VadTuning(Threshold: 0.4f));
            Assert.Equal("0.4", args[1]); // must be "0.4", never "0,4"
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void AppendVadTuning_MinSpeechMs_EmitsFlagAndValue()
    {
        var args = new List<string>();
        WhisperProvider.AppendVadTuning(args, new VadTuning(MinSpeechMs: 300));
        Assert.Equal(new[] { "--vad-min-speech-duration-ms", "300" }, args);
    }

    [Fact]
    public void AppendVadTuning_MinSilenceMs_EmitsFlagAndValue()
    {
        var args = new List<string>();
        WhisperProvider.AppendVadTuning(args, new VadTuning(MinSilenceMs: 150));
        Assert.Equal(new[] { "--vad-min-silence-duration-ms", "150" }, args);
    }

    [Fact]
    public void AppendVadTuning_MaxSpeechS_ExactlyZero_NotEmitted()
    {
        // Emission rule: MaxSpeechS > 0f (not >= 0). 0 is not a valid max-speech duration
        // and must not be emitted any more than the -1 sentinel would be.
        var args = new List<string>();
        WhisperProvider.AppendVadTuning(args, new VadTuning(MaxSpeechS: 0f));
        Assert.Empty(args);
    }

    [Fact]
    public void AppendVadTuning_MaxSpeechS_Positive_EmitsFlagAndValue()
    {
        var args = new List<string>();
        WhisperProvider.AppendVadTuning(args, new VadTuning(MaxSpeechS: 12.5f));
        Assert.Equal(new[] { "--vad-max-speech-duration-s", "12.5" }, args);
    }

    [Fact]
    public void AppendVadTuning_SpeechPadMs_EmitsFlagAndValue()
    {
        var args = new List<string>();
        WhisperProvider.AppendVadTuning(args, new VadTuning(SpeechPadMs: 50));
        Assert.Equal(new[] { "--vad-speech-pad-ms", "50" }, args);
    }

    [Fact]
    public void AppendVadTuning_SamplesOverlap_EmitsFlagAndValue()
    {
        var args = new List<string>();
        WhisperProvider.AppendVadTuning(args, new VadTuning(SamplesOverlap: 0.1f));
        Assert.Equal(new[] { "--vad-samples-overlap", "0.1" }, args);
    }

    [Fact]
    public void AppendVadTuning_AllFieldsSet_EmitsAllSixFlagsInDocumentedOrder()
    {
        var tuning = new VadTuning(
            Threshold: 0.6f,
            MinSpeechMs: 250,
            MinSilenceMs: 100,
            MaxSpeechS: 30f,
            SpeechPadMs: 30,
            SamplesOverlap: 0.1f);

        var args = new List<string>();
        WhisperProvider.AppendVadTuning(args, tuning);

        Assert.Equal(new[]
        {
            "--vad-threshold",            "0.6",
            "--vad-min-speech-duration-ms", "250",
            "--vad-min-silence-duration-ms", "100",
            "--vad-max-speech-duration-s", "30",
            "--vad-speech-pad-ms",        "30",
            "--vad-samples-overlap",      "0.1",
        }, args);
    }

    // ── BuildTranscribeArguments with tuning ──────────────────────────────────

    [Fact]
    public void BuildTranscribeArguments_TuningAppearsRightAfterVadModelPath()
    {
        var tuning = new VadTuning(Threshold: 0.4f);
        var args = BuildWithTuning("/x/vad.bin", tuning);

        var vadModelIdx = IndexOf(args, "--vad-model");
        Assert.True(vadModelIdx >= 0, "--vad-model must be present when vadModelPath is set");
        Assert.Equal("/x/vad.bin", args[vadModelIdx + 1]);
        // Tuning flags appear immediately after the model path, before -osrt.
        Assert.Equal("--vad-threshold", args[vadModelIdx + 2]);
        Assert.Equal("0.4", args[vadModelIdx + 3]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void BuildTranscribeArguments_NoVadModel_TuningSuppressedEntirely(string? vadModelPath)
    {
        // Without a VAD model the entire --vad block (including tuning) must be absent.
        var tuning = new VadTuning(Threshold: 0.9f, SpeechPadMs: 50);
        var args = BuildWithTuning(vadModelPath, tuning);

        Assert.DoesNotContain("--vad", args);
        Assert.DoesNotContain("--vad-threshold", args);
        Assert.DoesNotContain("--vad-speech-pad-ms", args);
    }

    [Fact]
    public void BuildTranscribeArguments_NullTuning_OutputIdenticalToUnset()
    {
        // null tuning must produce exactly the same args as VadTuning.Unset.
        var withNull  = BuildWithTuning("/x/vad.bin", null);
        var withUnset = BuildWithTuning("/x/vad.bin", VadTuning.Unset);
        Assert.Equal(withNull, withUnset);
    }

    // ── IsManagedVadPath ──────────────────────────────────────────────────────

    [Fact]
    public void IsManagedVadPath_FileDirectlyUnderVadDir_ReturnsTrue()
    {
        var vadDir   = Path.Combine(Path.GetTempPath(), "vad-managed-" + Guid.NewGuid().ToString("N"));
        var filePath = Path.Combine(vadDir, "ggml-silero-v5.1.2.bin");
        Assert.True(WhisperSetupService.IsManagedVadPath(filePath, vadDir));
    }

    [Fact]
    public void IsManagedVadPath_FileInDifferentDirectory_ReturnsFalse()
    {
        var vadDir   = "/data/whisper/vad";
        var filePath = "/tmp/custom-vad.bin";
        Assert.False(WhisperSetupService.IsManagedVadPath(filePath, vadDir));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsManagedVadPath_NullOrWhitespacePath_ReturnsFalse(string? path)
    {
        Assert.False(WhisperSetupService.IsManagedVadPath(path, "/some/vad/dir"));
    }

    // ── VadModelPathFor ───────────────────────────────────────────────────────

    [Fact]
    public void VadModelPathFor_ReturnsVadDirectoryPlusFileName()
    {
        var service  = CreateService(out _);
        var expected = Path.Combine(service.VadDirectory, "ggml-silero-v6.2.0.bin");
        Assert.Equal(expected, service.VadModelPathFor("ggml-silero-v6.2.0.bin"));
    }

    // ── Version-aware ResolveVadModelPath ─────────────────────────────────────

    [Fact]
    public void ResolveVadModelPath_ExternalOverride_WinsEvenWhenVersionFileAlsoExists()
    {
        // A custom file outside vad/ is a genuine external override: it must win over the
        // managed version file, regardless of which version key is selected.
        var service = CreateService(out _);
        Directory.CreateDirectory(service.VadDirectory);

        var externalFile = Path.Combine(Path.GetTempPath(),
            "custom-vad-" + Guid.NewGuid().ToString("N") + ".bin");
        File.WriteAllText(externalFile, "x");
        File.WriteAllText(service.VadModelPath, "x"); // managed v5.1.2 also present

        try
        {
            Assert.Equal(externalFile, service.ResolveVadModelPath(externalFile, "v5.1.2"));
        }
        finally
        {
            if (File.Exists(externalFile)) File.Delete(externalFile);
            if (Directory.Exists(service.VadDirectory))
                Directory.Delete(service.VadDirectory, recursive: true);
        }
    }

    [Fact]
    public void ResolveVadModelPath_VersionSwitch_ReturnsSelectedVersionFile()
    {
        // When versionKey:"v6.2.0" and only v6.2.0 is on disk, that path is returned.
        var service = CreateService(out _);
        Directory.CreateDirectory(service.VadDirectory);

        var v620Path = service.VadModelPathFor("ggml-silero-v6.2.0.bin");
        File.WriteAllText(v620Path, "x");

        try
        {
            Assert.Equal(v620Path, service.ResolveVadModelPath(null, "v6.2.0"));
        }
        finally
        {
            if (Directory.Exists(service.VadDirectory))
                Directory.Delete(service.VadDirectory, recursive: true);
        }
    }

    [Fact]
    public void ResolveVadModelPath_StaleManagedConfigPath_VersionFileTakesPrecedence()
    {
        // User switches from v5.1.2 to v6.2.0. The configuredPath still points at the old
        // managed v5.1.2 file (inside vad/). IsManagedVadPath detects it as managed, so
        // rule 1 skips it; rule 2 finds the selected v6.2.0 file and returns it.
        var service = CreateService(out _);
        Directory.CreateDirectory(service.VadDirectory);

        var v512Path = service.VadModelPath;                             // old managed path
        var v620Path = service.VadModelPathFor("ggml-silero-v6.2.0.bin");
        File.WriteAllText(v512Path, "x");
        File.WriteAllText(v620Path, "x");

        try
        {
            Assert.Equal(v620Path, service.ResolveVadModelPath(v512Path, "v6.2.0"));
        }
        finally
        {
            if (Directory.Exists(service.VadDirectory))
                Directory.Delete(service.VadDirectory, recursive: true);
        }
    }

    [Fact]
    public void ResolveVadModelPath_SelectedVersionMissing_FallsBackToConfiguredPath()
    {
        // v6.2.0 is selected but not yet downloaded. The configured path (old v5.1.2 managed
        // file) still exists, so rule 3 returns it as a last resort.
        var service = CreateService(out _);
        Directory.CreateDirectory(service.VadDirectory);

        var v512Path = service.VadModelPath; // managed, exists
        File.WriteAllText(v512Path, "x");
        // v6.2.0 file is NOT on disk

        try
        {
            Assert.Equal(v512Path, service.ResolveVadModelPath(v512Path, "v6.2.0"));
        }
        finally
        {
            if (Directory.Exists(service.VadDirectory))
                Directory.Delete(service.VadDirectory, recursive: true);
        }
    }

    [Fact]
    public void ResolveVadModelPath_NothingPresentAndVersionSelected_ReturnsNull()
    {
        var service = CreateService(out _);
        // Fresh temp data dir — no files on disk, no configured path.
        Assert.Null(service.ResolveVadModelPath(null, "v6.2.0"));
    }

    // ── BuildVadTuning: config → VadTuning mapping (transposition guard) ───────

    [Fact]
    public void BuildVadTuning_MapsEachConfigFieldToItsSlot()
    {
        // Deliberately distinct values so a swapped field would fail an assertion.
        var config = new PluginConfiguration
        {
            VadThreshold = 0.31f,
            VadMinSpeechDurationMs = 211,
            VadMinSilenceDurationMs = 122,
            VadMaxSpeechDurationS = 33.5f,
            VadSpeechPadMs = 44,
            VadSamplesOverlap = 0.25f,
        };

        var tuning = SubtitleProviderFactory.BuildVadTuning(config);

        Assert.Equal(0.31f, tuning.Threshold);
        Assert.Equal(211, tuning.MinSpeechMs);
        Assert.Equal(122, tuning.MinSilenceMs);
        Assert.Equal(33.5f, tuning.MaxSpeechS);
        Assert.Equal(44, tuning.SpeechPadMs);
        Assert.Equal(0.25f, tuning.SamplesOverlap);
    }

    [Fact]
    public void BuildVadTuning_DefaultConfig_EqualsUnset()
    {
        // A default (untouched) config must produce the no-op tuning → zero --vad-* flags emitted.
        Assert.Equal(VadTuning.Unset, SubtitleProviderFactory.BuildVadTuning(new PluginConfiguration()));
    }

    [Fact]
    public void AppendVadTuning_Threshold_Zero_IsEmitted()
    {
        // Threshold uses a >= 0 guard (unlike MaxSpeechS's > 0), so an explicit 0 IS passed through —
        // guards the sentinel semantics if someone ever tightens the guard to > 0.
        var args = new List<string>();
        WhisperProvider.AppendVadTuning(args, new VadTuning(Threshold: 0f));
        Assert.Equal(new[] { "--vad-threshold", "0" }, args);
    }
}
