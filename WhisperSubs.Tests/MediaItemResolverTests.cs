using WhisperSubs.Controller;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Locks the allow-list contract of <see cref="MediaItemResolver.IsAllowedGenerateTarget"/> — the guard
/// both the admin GenerateAll path and the user-request path use to decide what a "Generate all" may fan
/// out over. Only media leaves (Video and its subclasses Movie/Episode; Audio) and the intended containers
/// (Series, Season, MusicAlbum) are allowed; a BoxSet collection, MusicArtist, plain Folder, or a
/// whole-library container (CollectionFolder / UserView / AggregateFolder) is rejected so an admin can't
/// trigger an uncapped recursive fan-out over the entire library. Entity types are the concrete Jellyfin
/// runtime classes (fully qualified to sidestep the Entities.Audio namespace-vs-type name collision).
/// </summary>
public class MediaItemResolverTests
{
    // ── Allowed: media leaves ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Video_IsAllowed()
        => Assert.True(MediaItemResolver.IsAllowedGenerateTarget(new MediaBrowser.Controller.Entities.Video()));

    [Fact]
    public void Movie_IsAllowed()
        // Movie : Video — the single-movie case.
        => Assert.True(MediaItemResolver.IsAllowedGenerateTarget(new MediaBrowser.Controller.Entities.Movies.Movie()));

    [Fact]
    public void Episode_IsAllowed()
        // Episode : Video — the single-episode case.
        => Assert.True(MediaItemResolver.IsAllowedGenerateTarget(new MediaBrowser.Controller.Entities.TV.Episode()));

    [Fact]
    public void Audio_IsAllowed()
        // Audio leaf — only actually enqueued when lyrics generation is enabled, but the type is allowed.
        => Assert.True(MediaItemResolver.IsAllowedGenerateTarget(new MediaBrowser.Controller.Entities.Audio.Audio()));

    // ── Allowed: intended containers ─────────────────────────────────────────────────────────────

    [Fact]
    public void Series_IsAllowed()
        => Assert.True(MediaItemResolver.IsAllowedGenerateTarget(new MediaBrowser.Controller.Entities.TV.Series()));

    [Fact]
    public void Season_IsAllowed()
        => Assert.True(MediaItemResolver.IsAllowedGenerateTarget(new MediaBrowser.Controller.Entities.TV.Season()));

    [Fact]
    public void MusicAlbum_IsAllowed()
        => Assert.True(MediaItemResolver.IsAllowedGenerateTarget(new MediaBrowser.Controller.Entities.Audio.MusicAlbum()));

    // ── Rejected: collection/artist/folder floods ────────────────────────────────────────────────

    [Fact]
    public void BoxSet_IsRejected()
        // A collection derives from Folder, not Video/Audio — GenerateAll on it would sweep every member.
        => Assert.False(MediaItemResolver.IsAllowedGenerateTarget(new MediaBrowser.Controller.Entities.Movies.BoxSet()));

    [Fact]
    public void MusicArtist_IsRejected()
        // MusicArtist : Folder (NOT MusicAlbum) — rejecting it caps the fan-out at album granularity.
        => Assert.False(MediaItemResolver.IsAllowedGenerateTarget(new MediaBrowser.Controller.Entities.Audio.MusicArtist()));

    [Fact]
    public void Folder_IsRejected()
        => Assert.False(MediaItemResolver.IsAllowedGenerateTarget(new MediaBrowser.Controller.Entities.Folder()));

    // ── Rejected: whole-library containers (the old reject-list, now subsumed) ────────────────────

    [Fact]
    public void CollectionFolder_IsRejected()
        => Assert.False(MediaItemResolver.IsAllowedGenerateTarget(new MediaBrowser.Controller.Entities.CollectionFolder()));

    [Fact]
    public void AggregateFolder_IsRejected()
        => Assert.False(MediaItemResolver.IsAllowedGenerateTarget(new MediaBrowser.Controller.Entities.AggregateFolder()));
}
