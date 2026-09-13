using System.Text.RegularExpressions;
using WhisperSubs.Web;
using Xunit;

namespace WhisperSubs.Tests;

// Issue #108 / File Transformation 3.0 compatibility: FT's registry keys transformation
// pipelines by the raw fileNamePattern STRING and, per request, tries an exact dictionary match
// against the served path before ever falling back to walking keys as regexes. Other ecosystem
// plugins (Moonfin/Moonbase, PluginPages/JellyfinEnhanced) register the bare literal "index.html"
// as their pattern, which becomes an exact-match key that wins and short-circuits the regex pass —
// so a differently-spelled equivalent pattern (the previous end-anchored "(^|/)index\.html$")
// registers successfully but is silently never invoked whenever another plugin already owns the
// literal "index.html" key. WhisperSubs' pattern MUST be that same literal string to land in the
// shared pipeline bucket. It still doubles as an unanchored regex fallback for nested paths.
public class FileNamePatternTests
{
    [Fact]
    public void IndexFileNamePattern_IsTheBareLiteralFileTransformationConventionUses()
    {
        Assert.Equal("index.html", WebFileTransformation.IndexFileNamePattern);
    }

    [Theory]
    [InlineData("index.html")]
    [InlineData("web/index.html")]
    [InlineData("/jellyfin/web/index.html")]
    public void IndexFileNamePattern_MatchesServedIndexPathsAsRegexFallback(string servedPath)
    {
        Assert.Matches(new Regex(WebFileTransformation.IndexFileNamePattern), servedPath);
    }
}
