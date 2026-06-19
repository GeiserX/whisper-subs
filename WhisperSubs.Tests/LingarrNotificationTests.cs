using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using WhisperSubs.Controller;
using Xunit;

namespace WhisperSubs.Tests;

public class LingarrNotificationTests
{
    [Fact]
    public void GetLingarrWebhookPath_Movie_ReturnsRadarrPath()
    {
        var item = new Movie { Name = "Test Movie" };
        var path = SubtitleManager.GetLingarrWebhookPath(item);
        Assert.Equal("/api/webhook/radarr", path);
    }

    [Fact]
    public void GetLingarrWebhookPath_Episode_ReturnsSonarrPath()
    {
        var item = new Episode { Name = "Test Episode" };
        var path = SubtitleManager.GetLingarrWebhookPath(item);
        Assert.Equal("/api/webhook/sonarr", path);
    }

    [Fact]
    public void GetLingarrWebhookPath_Audio_ReturnsNull()
    {
        var item = new MediaBrowser.Controller.Entities.Audio.Audio { Name = "Test Track" };
        var path = SubtitleManager.GetLingarrWebhookPath(item);
        Assert.Null(path);
    }

    [Fact]
    public void GetLingarrWebhookPath_Video_ReturnsNull()
    {
        var item = new Video { Name = "Test Video" };
        var path = SubtitleManager.GetLingarrWebhookPath(item);
        Assert.Null(path);
    }
}
