using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using WhisperSubs.Controller;
using Xunit;

namespace WhisperSubs.Tests;

public class LingarrNotificationTests
{
    [Fact]
    public void ResolveLingarrMediaType_Movie_ReturnsMovie()
    {
        var item = new Movie { Name = "Test Movie" };
        Assert.Equal("Movie", SubtitleManager.ResolveLingarrMediaType(item));
    }

    [Fact]
    public void ResolveLingarrMediaType_Episode_ReturnsEpisode()
    {
        var item = new Episode { Name = "Test Episode" };
        Assert.Equal("Episode", SubtitleManager.ResolveLingarrMediaType(item));
    }

    [Fact]
    public void ResolveLingarrMediaType_Audio_ReturnsNull()
    {
        var item = new MediaBrowser.Controller.Entities.Audio.Audio { Name = "Test Track" };
        Assert.Null(SubtitleManager.ResolveLingarrMediaType(item));
    }

    [Fact]
    public void ResolveLingarrMediaType_Video_ReturnsNull()
    {
        var item = new Video { Name = "Test Video" };
        Assert.Null(SubtitleManager.ResolveLingarrMediaType(item));
    }
}
