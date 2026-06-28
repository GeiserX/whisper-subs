using WhisperSubs.Controller;
using Xunit;

namespace WhisperSubs.Tests;

// Issue #101: generated subtitles must save to Jellyfin's internal metadata path when the media
// library is read-only OR "Save subtitles into media folders" is off, so they still save and play
// on read-only libraries (mirroring how OpenSubtitles/Jellyfin store subs for those libraries).
// These pin the pure directory-choice; the disk I/O wrappers are [ExcludeFromCodeCoverage].
public class SubtitleSavePathTests
{
    private const string Media = "/media/Movies/Film (2024)";
    private const string Meta = "/config/metadata/library/aa/0123456789abcdef";

    [Fact]
    public void SaveWithMedia_AndWritable_UsesMediaFolder()
    {
        // The only case that keeps today's behaviour: opted in AND the folder is writable.
        Assert.Equal(Media, SubtitleManager.ChooseSubtitleDirectory(Media, Meta, saveWithMedia: true, mediaDirectoryWritable: true));
    }

    [Fact]
    public void SaveWithMedia_ButReadOnly_FallsBackToMetadata()
    {
        // The reported case: opted in, but the media folder is read-only → use the metadata path.
        Assert.Equal(Meta, SubtitleManager.ChooseSubtitleDirectory(Media, Meta, saveWithMedia: true, mediaDirectoryWritable: false));
    }

    [Fact]
    public void SaveWithMediaOff_Writable_UsesMetadata()
    {
        // "Save subtitles into media folders" off → metadata path even when the folder is writable,
        // exactly like OpenSubtitles. (The issue author's configuration.)
        Assert.Equal(Meta, SubtitleManager.ChooseSubtitleDirectory(Media, Meta, saveWithMedia: false, mediaDirectoryWritable: true));
    }

    [Fact]
    public void SaveWithMediaOff_ReadOnly_UsesMetadata()
    {
        Assert.Equal(Meta, SubtitleManager.ChooseSubtitleDirectory(Media, Meta, saveWithMedia: false, mediaDirectoryWritable: false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoMetadataPath_FallsBackToMedia_NeverEmpty(string? meta)
    {
        // Defensive: if the internal metadata path can't be resolved, never return an empty directory —
        // fall back to the media folder (best effort) rather than writing to "".
        Assert.Equal(Media, SubtitleManager.ChooseSubtitleDirectory(Media, meta!, saveWithMedia: false, mediaDirectoryWritable: false));
    }
}
