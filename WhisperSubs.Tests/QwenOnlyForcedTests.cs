using Microsoft.Extensions.Logging.Abstractions;
using WhisperSubs.Controller;
using WhisperSubs.Providers;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// On a Qwen3-ASR title, a forced line in a language the model does not cover is left out instead of
/// starting this server's Whisper large-v3 (about 6 GB on the GPU) for what has so far always been Spanish
/// misread by the small detection model.
/// </summary>
public class QwenOnlyForcedTests
{
    [Theory]
    [InlineData(true, false, "gl", true)]     // Galician, Catalan, Norwegian: left out on a Qwen3-ASR title
    [InlineData(true, false, "ca", true)]
    [InlineData(true, false, "nn", true)]
    [InlineData(true, false, "en", false)]    // a language Qwen3-ASR covers is transcribed
    [InlineData(true, false, "es", false)]
    [InlineData(true, true, "gl", false)]     // English translation of a foreign line has no Qwen3 task
    [InlineData(false, false, "gl", false)]   // a Whisper title keeps every line
    [InlineData(true, false, "auto", false)]  // no language named: nothing to judge
    [InlineData(true, false, null, false)]
    public void SkipsUncoveredForcedLine(bool qwenTranscribes, bool translate, string? language, bool skipped)
        => Assert.Equal(skipped, SubtitleManager.SkipsUncoveredForcedLine(qwenTranscribes, translate, language));

    [Fact]
    public void QwenTranscribes_ForServerRows_NotForPlainWhisper()
    {
        var whisper = new WhisperProvider(NullLogger<WhisperProvider>.Instance, "/nope/whisper.bin", "/nope/whisper-cli", 0, "", "", "", null, 0);
        var akouRow = new HostAssistedProvider(new RemoteWhisperProvider(NullLogger.Instance, "http://a:8476", "best", dialect: "akou"), whisper);

        var qwen = new Qwen3Provider(NullLogger.Instance, "/nope/crispasr", "/nope/qwen3.gguf", 0, "", null, 0, "/tmp");
        Assert.True(SubtitleManager.QwenTranscribes(new EngineSwitchProvider(whisper, () => qwen)));
        Assert.False(SubtitleManager.QwenTranscribes(new EngineSwitchProvider(whisper, () => null)));   // Whisper engine selected
        Assert.True(SubtitleManager.QwenTranscribes(akouRow));
        Assert.False(SubtitleManager.QwenTranscribes(whisper));
        Assert.False(SubtitleManager.QwenTranscribes(new RemoteWhisperProvider(NullLogger.Instance, "http://w:9010", "large-v3")));
    }

    [Fact]
    public void DetectionGate_AnUnreadablePathIsComparedAsWritten()
        => Assert.Same(HostEngineGates.Detection, WhisperProvider.GateForDetection("/m/ba\0d.bin", "/m/ggml-large-v3.bin"));
}
