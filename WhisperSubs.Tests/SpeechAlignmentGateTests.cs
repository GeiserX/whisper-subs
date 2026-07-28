using WhisperSubs.Configuration;
using WhisperSubs.Controller;
using Xunit;

namespace WhisperSubs.Tests;

// Issue #78: the speech-onset forward-snap was disabled whenever VAD was on, so on default installs
// (VAD on) it never ran — and VAD improves transcription, not whisper's tendency to start a cue
// slightly early. ShouldAlignToSpeech gates it: run when alignment is enabled AND (VAD off OR the
// user opted into layering it on top of VAD).
public class SpeechAlignmentGateTests
{
    [Theory]
    // alignEnabled, requiresOptIn, alignWithVad => expected
    [InlineData(true, false, false, true)]   // VAD off → run (the classic fallback)
    [InlineData(true, false, true, true)]    // VAD off, opt-in irrelevant → run
    [InlineData(true, true, false, false)]   // VAD on, no opt-in → SKIP (default behaviour, unchanged)
    [InlineData(true, true, true, true)]     // VAD on + opt-in → run (the #78 fix)
    [InlineData(false, false, false, false)] // alignment disabled → never run
    [InlineData(false, true, false, false)]  // alignment disabled (VAD on) → never run
    [InlineData(false, true, true, false)]   // alignment disabled overrides the opt-in
    [InlineData(false, false, true, false)]
    public void ShouldAlignToSpeech_Matrix(bool alignEnabled, bool requiresOptIn, bool alignWithVad, bool expected)
    {
        Assert.Equal(expected, SubtitleManager.ShouldAlignToSpeech(alignEnabled, requiresOptIn, alignWithVad));
    }

    [Fact]
    public void AlignSubtitlesToSpeechWithVad_DefaultsOff()
    {
        // Default must be off so the opt-in never changes existing VAD installs without consent.
        Assert.False(new PluginConfiguration().AlignSubtitlesToSpeechWithVad);
    }

    [Theory]
    [InlineData(false, 5, 0)]
    [InlineData(true, 0.05, 0)]
    [InlineData(true, 5, 5)]
    [InlineData(true, 600, 0)]
    public void EffectiveAudioOffset_UsesTheSameEligibilityForFreshAndResume(
        bool enabled, double audioStartTime, double expected)
    {
        Assert.Equal(expected, SubtitleManager.EffectiveAudioOffset(enabled, audioStartTime));
    }

    [Theory]
    [InlineData(10, 4.936, 0, 4.936, 10)]
    [InlineData(6.4, 1.4, 1.4, 1.4, 5)]
    [InlineData(5, 1.4, 1.4, 0, 5)]
    [InlineData(1, 0, 5, 0, 0)]
    public void ResumeExtractionOffset_ConvertsCompensatedPlaybackToInputSeekTime(
        double resumePlaybackSeconds,
        double audioStartTime,
        double containerStartTime,
        double effectiveCompensation,
        double expected)
    {
        Assert.Equal(
            expected,
            SubtitleManager.ResumeExtractionOffset(
                resumePlaybackSeconds,
                audioStartTime,
                containerStartTime,
                effectiveCompensation),
            precision: 6);
    }

    [Fact]
    public void ResolveAudioStreamIndex_UsesRequestedLanguageOrDefaultDisposition()
    {
        const string ffprobeJson =
            """
            {
              "streams": [
                { "disposition": { "default": 1 }, "tags": { "language": "eng" } },
                { "disposition": { "default": 0 }, "tags": { "language": "spa" } }
              ]
            }
            """;

        Assert.Equal(0, SubtitleManager.ResolveAudioStreamIndex(ffprobeJson, "auto"));
        Assert.Equal(1, SubtitleManager.ResolveAudioStreamIndex(ffprobeJson, "es"));
        Assert.Equal(0, SubtitleManager.ResolveAudioStreamIndex(ffprobeJson, "fr"));
        Assert.Equal(0, SubtitleManager.ResolveAudioStreamIndex(
            """{"streams":[{"tags":{"language":"eng"}},{"tags":{"language":"spa"}}]}""",
            "auto"));
        Assert.Equal(-1, SubtitleManager.ResolveAudioStreamIndex("""{"streams":[]}""", "auto"));
        Assert.Equal(-1, SubtitleManager.ResolveAudioStreamIndex("not-json", "auto"));
    }
}
