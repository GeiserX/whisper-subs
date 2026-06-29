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
    // alignEnabled, providerUsesVad, alignWithVad => expected
    [InlineData(true, false, false, true)]   // VAD off → run (the classic fallback)
    [InlineData(true, false, true, true)]    // VAD off, opt-in irrelevant → run
    [InlineData(true, true, false, false)]   // VAD on, no opt-in → SKIP (default behaviour, unchanged)
    [InlineData(true, true, true, true)]     // VAD on + opt-in → run (the #78 fix)
    [InlineData(false, false, false, false)] // alignment disabled → never run
    [InlineData(false, true, false, false)]  // alignment disabled (VAD on) → never run
    [InlineData(false, true, true, false)]   // alignment disabled overrides the opt-in
    [InlineData(false, false, true, false)]
    public void ShouldAlignToSpeech_Matrix(bool alignEnabled, bool usesVad, bool alignWithVad, bool expected)
    {
        Assert.Equal(expected, SubtitleManager.ShouldAlignToSpeech(alignEnabled, usesVad, alignWithVad));
    }

    [Fact]
    public void AlignSubtitlesToSpeechWithVad_DefaultsOff()
    {
        // Default must be off so the opt-in never changes existing VAD installs without consent.
        Assert.False(new PluginConfiguration().AlignSubtitlesToSpeechWithVad);
    }
}
