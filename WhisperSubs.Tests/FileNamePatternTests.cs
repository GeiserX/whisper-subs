using System.Text.RegularExpressions;
using WhisperSubs.Web;
using Xunit;

namespace WhisperSubs.Tests;

// Issue #108: File Transformation treats fileNamePattern as an UNANCHORED regex over the served
// path, so our pattern must be end-anchored with an escaped dot — matching index.html under any
// prefix while rejecting lookalikes. These pins guard the exact semantics the registration relies on.
public class FileNamePatternTests
{
    [Theory]
    [InlineData("index.html")]
    [InlineData("web/index.html")]
    [InlineData("/jellyfin/web/index.html")]
    public void IndexFileNamePattern_MatchesServedIndexPaths(string servedPath)
    {
        Assert.Matches(new Regex(WebFileTransformation.IndexFileNamePattern), servedPath);
    }

    [Theory]
    [InlineData("index2html")]           // "." must not act as any-char
    [InlineData("index.htmlx")]          // must be end-anchored
    [InlineData("web/index.html.bak")]
    [InlineData("reindex.html")]         // "index.html" must be preceded by start-of-path or "/"
    public void IndexFileNamePattern_RejectsLookalikes(string servedPath)
    {
        Assert.DoesNotMatch(new Regex(WebFileTransformation.IndexFileNamePattern), servedPath);
    }
}
