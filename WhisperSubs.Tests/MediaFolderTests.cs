using System;
using System.IO;
using System.Linq;
using WhisperSubs.Controller;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// <see cref="MediaFolder.GetFiles"/> must answer exactly what <see cref="Directory.GetFiles(string, string)"/>
/// answers, links and all, while reading the names without following every link (the 3-to-7-minute
/// listings of a movie folder full of links into a network share).
/// </summary>
public sealed class MediaFolderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mediafolder_" + Guid.NewGuid().ToString("N"));

    public MediaFolderTests()
    {
        Directory.CreateDirectory(_dir);
        Touch("Movie (1999) [BDRip 1080p][HDO].mkv");
        Touch("Movie (1999) [BDRip 1080p][HDO].es.WhisperSubs.srt");
        Touch("Movie (1999) [BDRip 1080p][HDO].es.WhisperSubs.forced.srt");
        Touch("Movie (1999) [BDRip 1080p][HDO].es.forced.noforeignlang");
        Touch("Movie (1999) [BDRip 1080p][HDO].en.srt");
        Touch("movie (1999) [bdrip 1080p][hdo].fr.srt");          // other case: not a match on Linux
        Touch("Película ñ.es.srt");
        Touch(".hidden.es.srt");
        Touch("Track 01.lrc");
        Touch("Track 01.es.lrc");
        Directory.CreateDirectory(Path.Combine(_dir, "Movie (1999) [BDRip 1080p][HDO].extras.srt"));   // a folder that matches
        Directory.CreateDirectory(Path.Combine(_dir, "target-dir"));
        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(Path.Combine(_dir, "Movie (1999) [BDRip 1080p][HDO] - 720p.mp4"), Path.Combine(_dir, "Movie (1999) [BDRip 1080p][HDO].mkv"));
            File.CreateSymbolicLink(Path.Combine(_dir, "Movie (1999) [BDRip 1080p][HDO].de.srt"), Path.Combine(_dir, "Movie (1999) [BDRip 1080p][HDO].en.srt"));
            Directory.CreateSymbolicLink(Path.Combine(_dir, "Movie (1999) [BDRip 1080p][HDO].linkdir.srt"), Path.Combine(_dir, "target-dir"));
            File.CreateSymbolicLink(Path.Combine(_dir, "Movie (1999) [BDRip 1080p][HDO].it.srt"), Path.Combine(_dir, "missing", "gone.srt"));   // broken
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Theory]
    [InlineData("Movie (1999) [BDRip 1080p][HDO].*.srt")]
    [InlineData("Movie (1999) [BDRip 1080p][HDO].*")]
    [InlineData("Movie (1999) [BDRip 1080p][HDO].*.forced.noforeignlang")]
    [InlineData("Película ñ.*.srt")]
    [InlineData("Track 01.*.lrc")]
    [InlineData("*.srt")]
    [InlineData("*")]
    [InlineData("nothing.*")]
    public void SameAnswerAsDirectoryGetFiles(string pattern)
    {
        var expected = Directory.GetFiles(_dir, pattern).OrderBy(p => p, StringComparer.Ordinal).ToArray();

        var actual = MediaFolder.GetFiles(_dir, pattern).OrderBy(p => p, StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void OnLinux_TheNamesComeFromReaddir_NotFromTheDotNetFallback()
    {
        // A fallback to Directory.GetFiles would pass every equivalence test above and still be slow, so prove
        // the fast path is the one that runs where it matters.
        if (!OperatingSystem.IsLinux()) return;

        var names = MediaFolder.ReadNames(_dir);

        Assert.NotNull(names);
        Assert.Contains("Movie (1999) [BDRip 1080p][HDO] - 720p.mp4", names!);   // a link, listed by its own name
        Assert.Contains("Movie (1999) [BDRip 1080p][HDO].it.srt", names!);       // a broken link too
        Assert.Contains("Película ñ.es.srt", names!);
    }

    [Fact]
    public void Matching_ChecksOnlyNamesThatMatch()
    {
        var asked = new System.Collections.Generic.List<string>();
        var names = new[] { ".", "..", "a.es.srt", "b.mkv", "a.en.srt", "a.dir.srt" };

        var files = MediaFolder.Matching("/m", names, "a.*.srt", path => { asked.Add(path); return path.EndsWith("a.dir.srt", StringComparison.Ordinal); });

        Assert.Equal(new[] { "/m/a.es.srt", "/m/a.en.srt" }, files);
        Assert.Equal(new[] { "/m/a.es.srt", "/m/a.en.srt", "/m/a.dir.srt" }, asked);   // b.mkv, "." and ".." never checked
    }

    [Fact]
    public void MissingFolder_ThrowsLikeDirectoryGetFiles()
    {
        var missing = Path.Combine(_dir, "no-such-folder");

        Assert.Throws<DirectoryNotFoundException>(() => MediaFolder.GetFiles(missing, "*.srt"));
    }

    private void Touch(string name) => File.WriteAllText(Path.Combine(_dir, name), "");
}
