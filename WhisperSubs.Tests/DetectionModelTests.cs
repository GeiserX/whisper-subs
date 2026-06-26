using System;
using System.IO;
using WhisperSubs.Providers;
using WhisperSubs.Setup;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Covers the dedicated language-detection model plumbing (issue #95): the catalog entry, the
/// setup-service path composition, and the WhisperProvider.ChooseDetectionModel selection that makes
/// forced-subtitle per-chunk --detect-language run on a small fast model (so it clears the per-chunk
/// timeout on slow no-AVX2 CPUs) while falling back to the transcription model when absent.
/// </summary>
public class DetectionModelTests
{
    private static WhisperSetupService CreateService(out string dataPath)
    {
        dataPath = Path.Combine(Path.GetTempPath(), "whispersubs-detect-" + Guid.NewGuid().ToString("N"));
        return new WhisperSetupService(new NullLogger<WhisperSetupService>(), dataPath);
    }

    [Fact]
    public void ModelCatalog_DetectionConstants_AreWellFormed()
    {
        Assert.EndsWith(".bin", ModelCatalog.DetectionModelFileName);
        Assert.StartsWith("https://", ModelCatalog.DetectionModelUrl);
        Assert.Contains("huggingface.co", ModelCatalog.DetectionModelUrl);
        Assert.EndsWith(ModelCatalog.DetectionModelFileName, ModelCatalog.DetectionModelUrl);
        Assert.True(ModelCatalog.DetectionModelSizeBytes > 0);
    }

    [Fact]
    public void DetectDirectory_And_DetectionModelPath_AreUnderDataPath()
    {
        var service = CreateService(out var dataPath);

        var expectedDetectDir = Path.Combine(dataPath, "whisper", "detect");
        Assert.Equal(expectedDetectDir, service.DetectDirectory);

        // Detection model lives in its OWN subdir, never in ModelsDirectory (so it can't be
        // mistaken for / displace the user's transcription model).
        Assert.NotEqual(service.ModelsDirectory, service.DetectDirectory);
        Assert.EndsWith(ModelCatalog.DetectionModelFileName, service.DetectionModelPath);
        Assert.Equal(service.DetectDirectory, Path.GetDirectoryName(service.DetectionModelPath));
    }

    [Fact]
    public void ChooseDetectionModel_PresentAndExists_ReturnsDetectionModel()
    {
        Assert.Equal(
            "/data/detect/ggml-base.bin",
            WhisperProvider.ChooseDetectionModel("/data/models/large-v3.bin", "/data/detect/ggml-base.bin", detectionModelExists: true));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ChooseDetectionModel_EmptyOrNull_FallsBackToTranscriptionModel(string? detectionPath)
    {
        Assert.Equal(
            "/data/models/large-v3.bin",
            WhisperProvider.ChooseDetectionModel("/data/models/large-v3.bin", detectionPath, detectionModelExists: false));
    }

    [Fact]
    public void ChooseDetectionModel_PathSetButNotYetDownloaded_FallsBackToTranscriptionModel()
    {
        // The factory hands the provider the expected detect/ location even before it's downloaded;
        // until the file actually exists, detection must use the transcription model (legacy behavior).
        Assert.Equal(
            "/data/models/large-v3.bin",
            WhisperProvider.ChooseDetectionModel("/data/models/large-v3.bin", "/data/detect/ggml-base.bin", detectionModelExists: false));
    }
}
