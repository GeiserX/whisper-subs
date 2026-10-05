using WhisperSubs.ScheduledTasks;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// "Pause generation during playback" used to count any session that held an item, paused or not. A TV
/// app parked on a paused title reports one for hours, so generation never ran on a busy server.
/// </summary>
public class PlaybackPauseSessionTests
{
    [Theory]
    [InlineData(true, false, true)]    // playing
    [InlineData(true, null, true)]     // client does not report a pause state: assume playing
    [InlineData(true, true, false)]    // paused on a title: not playback
    [InlineData(false, false, false)]  // no item
    [InlineData(false, null, false)]
    [InlineData(false, true, false)]
    public void IsPlaying_NeedsAnItemThatIsNotPaused(bool hasItem, bool? isPaused, bool expected)
    {
        Assert.Equal(expected, SubtitleGenerationTask.IsPlaying(hasItem, isPaused));
    }
}
