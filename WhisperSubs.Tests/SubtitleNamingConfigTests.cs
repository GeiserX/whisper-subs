using WhisperSubs.Configuration;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Pins the BRAND-FIRST defaults for the two new naming config fields so a fresh install produces
/// <c>Movie.en.WhisperSubs.srt</c> (label-first picker Title) out of the box.
/// </summary>
public class SubtitleNamingConfigTests
{
    [Fact]
    public void SubtitleLabel_DefaultsToWhisperSubs()
    {
        Assert.Equal("WhisperSubs", new PluginConfiguration().SubtitleLabel);
    }

    [Fact]
    public void SubtitleFilenameTemplate_DefaultsToBrandFirst()
    {
        Assert.Equal("{name}.{lang}.{label}{.type}", new PluginConfiguration().SubtitleFilenameTemplate);
    }
}
