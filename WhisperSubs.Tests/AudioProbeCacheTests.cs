using System;
using System.Collections.Generic;
using WhisperSubs.Controller;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// The remembered whisper probe per item: a hit only for the same file, so a replaced or re-encoded
/// file is probed again, and it survives a save/load round trip.
/// </summary>
public class AudioProbeCacheTests
{
    private static readonly Guid Item = Guid.NewGuid();
    private static readonly (long, long) File = (1_000_000L, 638_000_000_000_000_000L);

    [Fact]
    public void TryGet_SameFile_ReturnsTheRecordedProbe()
    {
        var cache = AudioProbeCache.InMemory();
        cache.Record(Item, File, " EN ", 0.93f);
        Assert.Equal(("en", 0.93f), cache.TryGet(Item, File));
    }

    // The invalidation rule: a different size or last-write time is a different file, so no answer.
    [Fact]
    public void TryGet_FileChanged_Misses()
    {
        var cache = AudioProbeCache.InMemory();
        cache.Record(Item, File, "en", 0.9f);
        Assert.Null(cache.TryGet(Item, (File.Item1 + 1, File.Item2)));
        Assert.Null(cache.TryGet(Item, (File.Item1, File.Item2 + 1)));
        Assert.Null(cache.TryGet(Item, null));
        Assert.Null(cache.TryGet(Guid.NewGuid(), File));
    }

    [Fact]
    public void Record_UnreadableFileOrBlankLanguage_RemembersNothing()
    {
        var cache = AudioProbeCache.InMemory();
        cache.Record(Item, null, "en", 0.9f);
        cache.Record(Item, File, " ", 0.9f);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Record_NewFile_ReplacesTheOldResult()
    {
        var cache = AudioProbeCache.InMemory();
        cache.Record(Item, File, "en", 0.9f);
        var replaced = (File.Item1 + 5, File.Item2 + 5);
        cache.Record(Item, replaced, "es", 0.8f);
        Assert.Null(cache.TryGet(Item, File));
        Assert.Equal(("es", 0.8f), cache.TryGet(Item, replaced));
    }

    [Fact]
    public void Json_RoundTrips_AndCorruptionOrOtherVersionsStartEmpty()
    {
        var cache = AudioProbeCache.InMemory();
        cache.Record(Item, File, "en", 0.9f);
        var back = AudioProbeCache.FromJson(cache.ToJson());
        Assert.Equal(("en", 0.9f), back.TryGet(Item, File));

        Assert.Equal(0, AudioProbeCache.FromJson("{not json").Count);
        Assert.Equal(0, AudioProbeCache.FromJson(cache.ToJson().Replace("\"Version\":1", "\"Version\":99")).Count);
        Assert.Equal(0, AudioProbeCache.FromJson(null).Count);
    }

    [Fact]
    public void PruneTo_DropsItemsTheSweepNoLongerSees()
    {
        var cache = AudioProbeCache.InMemory();
        var other = Guid.NewGuid();
        cache.Record(Item, File, "en", 0.9f);
        cache.Record(other, File, "es", 0.9f);
        Assert.Equal(1, cache.PruneTo(new HashSet<Guid> { Item }));
        Assert.NotNull(cache.TryGet(Item, File));
        Assert.Null(cache.TryGet(other, File));
    }
}
