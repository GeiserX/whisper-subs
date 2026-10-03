using System.Globalization;
using System.Threading;
using WhisperSubs.Controller;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// FFmpeg parses "-ss 1213,9" as an error. The seek and duration arguments must use a dot whatever
/// culture the server runs under; a Spanish Jellyfin wrote the comma and every resume failed.
/// </summary>
public class FfmpegSecondsCultureTests
{
    [Theory]
    [InlineData("es-ES")]
    [InlineData("de-DE")]
    [InlineData("en-US")]
    public void FfmpegSeconds_UsesADotUnderAnyCulture(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            Assert.Equal("1213.9", SubtitleManager.FfmpegSeconds(1213.9, 1));
            Assert.Equal("0.250", SubtitleManager.FfmpegSeconds(0.25, 3));
            Assert.Equal("1234567.000", SubtitleManager.FfmpegSeconds(1234567, 3));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    // The control: the plain call does produce the comma under es-ES, so the test above can fail.
    [Fact]
    public void PlainToString_ProducesTheCommaUnderSpanish()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("es-ES");
            Assert.Equal("1213,9", 1213.9.ToString("F1"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
