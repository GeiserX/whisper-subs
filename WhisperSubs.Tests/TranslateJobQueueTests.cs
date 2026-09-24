using System.Text.Json;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Moq;
using WhisperSubs.Controller;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Per-title translation queues a translate job, <c>(item, "auto", target)</c>, next to the existing
/// generate job, <c>(item, language)</c>. These pin the new identity, that it never merges with a generate
/// job or another target, that retries and restores keep the target, and that a normal job's key and
/// queue.json bytes are exactly what 4.9.0.1 produced. Fresh service instances, no Plugin.Instance, so
/// persistence is a disk-free no-op.
/// </summary>
public class TranslateJobQueueTests
{
    private static Video NewItem(string name = "Film") => new Video { Id = Guid.NewGuid(), Name = name };

    // ── Identity ────────────────────────────────────────────────────────────

    [Fact]
    public void IdentityKey_NormalJob_IsByteIdenticalTo491()
    {
        var id = Guid.NewGuid();
        var expected = $"{id:N}|auto";
        Assert.Equal(expected, SubtitleQueueService.IdentityKey(id, "auto"));
        Assert.Equal(expected, SubtitleQueueService.IdentityKey(id, "auto", null));
        Assert.Equal(expected, SubtitleQueueService.IdentityKey(id, "AUTO", "  "));
    }

    [Fact]
    public void IdentityKey_TranslateJob_DiffersFromGenerateAndPerTarget()
    {
        var id = Guid.NewGuid();
        var es = SubtitleQueueService.IdentityKey(id, "auto", "es");
        Assert.NotEqual(SubtitleQueueService.IdentityKey(id, "auto"), es);
        Assert.NotEqual(SubtitleQueueService.IdentityKey(id, "auto", "nl"), es);
        Assert.Equal(es, SubtitleQueueService.IdentityKey(id, "auto", "ES"));
        Assert.Equal($"{id:N}|auto|>es", es);
    }

    // ── Coexistence and merge ───────────────────────────────────────────────

    [Fact]
    public void TranslateAndGenerate_SameItem_Coexist()
    {
        var queue = new SubtitleQueueService();
        var item = NewItem();

        Assert.True(queue.Enqueue(item, "auto", PriorityTier.High));
        Assert.True(queue.Enqueue(item, "auto", PriorityTier.High, target: "es"));
        Assert.Equal(2, queue.PriorityCount);

        Assert.True(queue.TryDequeuePriority(out var first));
        Assert.True(queue.TryDequeuePriority(out var second));
        Assert.Null(first!.Target);          // FIFO within the tier: the generate job went in first
        Assert.Equal("es", second!.Target);
        Assert.Same(item, second.Item);
        Assert.Equal("auto", second.Language);
    }

    [Fact]
    public void TranslateJob_WhileGenerateInFlight_IsAccepted_AndViceVersa()
    {
        var queue = new SubtitleQueueService();
        var item = NewItem();

        Assert.True(queue.Enqueue(item, "auto", PriorityTier.High));
        Assert.True(queue.TryDequeuePriority(out _));                                      // generate running
        Assert.True(queue.Enqueue(item, "auto", PriorityTier.High, target: "es"));          // translate accepted
        Assert.False(queue.Enqueue(item, "auto", PriorityTier.High));                       // generate still running

        var other = NewItem("Other");
        Assert.True(queue.Enqueue(other, "auto", PriorityTier.High, target: "nl"));
        // Drain the first translate job, then the one for the other item: both are running now.
        Assert.True(queue.TryDequeuePriority(out var t1));
        Assert.True(queue.TryDequeuePriority(out var t2));
        Assert.Equal("es", t1!.Target);
        Assert.Equal("nl", t2!.Target);

        Assert.True(queue.Enqueue(other, "auto", PriorityTier.High));                      // generate accepted
        Assert.False(queue.Enqueue(other, "auto", PriorityTier.High, target: "nl"));       // same target running
    }

    [Fact]
    public void SameTarget_ReEnqueue_Merges()
    {
        var queue = new SubtitleQueueService();
        var item = NewItem();

        Assert.True(queue.Enqueue(item, "auto", PriorityTier.Medium, force: false, target: "es"));
        Assert.False(queue.Enqueue(item, "auto", PriorityTier.Critical, force: true, target: "ES"));
        Assert.Equal(1, queue.PriorityCount);

        Assert.True(queue.TryDequeuePriority(out var merged));
        Assert.True(merged!.Force);
        Assert.Equal(PriorityTier.Critical, merged.Tier);
        Assert.Equal("es", merged.Target);
    }

    [Fact]
    public void RetryOrRelease_And_RequeueWithoutRetry_KeepTarget()
    {
        var queue = new SubtitleQueueService();
        var item = NewItem();

        Assert.True(queue.Enqueue(item, "auto", PriorityTier.High, target: "es"));
        Assert.True(queue.TryDequeuePriority(out var running));
        Assert.True(queue.RetryOrRelease(running!, maxRetries: 3));

        Assert.True(queue.TryDequeuePriority(out var retried));
        Assert.Equal("es", retried!.Target);
        Assert.Equal(1, retried.RetryCount);

        queue.RequeueWithoutRetry(retried);
        Assert.True(queue.TryDequeuePriority(out var requeued));
        Assert.Equal("es", requeued!.Target);
        Assert.Equal(1, requeued.RetryCount);

        // The re-queued job holds the translate identity, so a generate job for the item is still free.
        Assert.True(queue.Enqueue(item, "auto", PriorityTier.High));
    }

    // ── queue.json ──────────────────────────────────────────────────────────

    [Fact]
    public void SerializeQueueFile_NormalJob_IsByteIdenticalTo491()
    {
        var item = NewItem();
        var wi = new SubtitleWorkItem { Item = item, Language = "auto", Force = true, Tier = PriorityTier.High };

        var json = SubtitleQueueService.SerializeQueueFile(
            new List<QueueEntry> { SubtitleQueueService.ToEntry(wi, (int)wi.Tier) }, new List<QueueEntry>());

        Assert.Equal(
            "{\"Version\":2,\"Pending\":[{\"ItemId\":\"" + item.Id.ToString("N") +
            "\",\"Language\":\"auto\",\"Force\":true,\"Tier\":1,\"RetryCount\":0}],\"InFlight\":[]}",
            json);
    }

    [Fact]
    public void SerializeQueueFile_TranslateJob_RoundTripsTarget()
    {
        var item = NewItem();
        var wi = new SubtitleWorkItem { Item = item, Language = "auto", Tier = PriorityTier.Medium, Target = "es", RetryCount = 2 };

        var json = SubtitleQueueService.SerializeQueueFile(
            new List<QueueEntry>(), new List<QueueEntry> { SubtitleQueueService.ToEntry(wi, (int)wi.Tier) });
        Assert.Contains("\"Target\":\"es\"", json);

        var (pending, inFlight) = SubtitleQueueService.ParseQueueFile(json);
        Assert.Empty(pending);
        Assert.Equal("es", Assert.Single(inFlight).Target);
        Assert.Equal(2, inFlight[0].RetryCount);
    }

    private static Func<Guid, BaseItem?> Resolver(params BaseItem[] items)
    {
        var byId = items.ToDictionary(i => i.Id);
        return id => byId.GetValueOrDefault(id);
    }

    [Fact]
    public void Restore_LegacyEntryWithoutTarget_RestoresAsNormalJob()
    {
        var a = NewItem("A");
        var b = NewItem("B");
        var v2 = "{\"Version\":2,\"Pending\":[{\"ItemId\":\"" + a.Id.ToString("N") +
                 "\",\"Language\":\"auto\",\"Force\":false,\"Tier\":1,\"RetryCount\":0}],\"InFlight\":[]}";
        var v1 = "[{\"ItemId\":\"" + b.Id.ToString("N") + "\",\"Language\":\"en\",\"Force\":true}]";

        foreach (var (json, item, lang) in new[] { (v2, a, "auto"), (v1, b, "en") })
        {
            var queue = new SubtitleQueueService();
            var (pending, inFlight) = SubtitleQueueService.ParseQueueFile(json);
            Assert.Null(pending[0].Target);

            var (restored, _) = queue.RestoreFromEntries(pending, inFlight, 3, Resolver(item));
            Assert.Equal(1, restored);

            // The old key: a fresh request for (item, lang) merges instead of queuing a second job.
            Assert.False(queue.Enqueue(item, lang, PriorityTier.High));
            Assert.True(queue.TryDequeuePriority(out var wi));
            Assert.Null(wi!.Target);
        }
    }

    [Fact]
    public void Restore_TranslateEntry_RestoresTargetAndKey()
    {
        var a = NewItem("A");
        var b = NewItem("B");
        var pending = new List<QueueEntry>
        {
            new() { ItemId = a.Id.ToString("N"), Language = "auto", Tier = (int)PriorityTier.High, Target = "es" },
        };
        var inFlight = new List<QueueEntry>
        {
            new() { ItemId = b.Id.ToString("N"), Language = "auto", Tier = (int)PriorityTier.Medium, RetryCount = 1, Target = "NL" },
        };

        var queue = new SubtitleQueueService();
        var (restored, givenUp) = queue.RestoreFromEntries(pending, inFlight, 3, Resolver(a, b));
        Assert.Equal(2, restored);
        Assert.Empty(givenUp);

        // Same translate identity: merges, does not add.
        Assert.False(queue.Enqueue(a, "auto", PriorityTier.High, target: "es"));
        Assert.False(queue.Enqueue(b, "auto", PriorityTier.High, target: "nl"));
        // The generate identity is separate.
        Assert.True(queue.Enqueue(a, "auto", PriorityTier.Low));

        Assert.True(queue.TryDequeuePriority(out var first));
        Assert.True(queue.TryDequeuePriority(out var second));
        Assert.Equal("es", first!.Target);
        Assert.Equal(0, first.RetryCount);
        Assert.Equal("nl", second!.Target);
        Assert.Equal(2, second.RetryCount);   // in-flight comes back at RetryCount + 1
    }

    [Fact]
    public void Restore_InvalidTarget_IsDropped()
    {
        var bad1 = NewItem("Bad1");
        var bad2 = NewItem("Bad2");
        var good = NewItem("Good");
        var pending = new List<QueueEntry>
        {
            new() { ItemId = bad1.Id.ToString("N"), Language = "auto", Tier = 1, Target = "xx" },
            new() { ItemId = bad2.Id.ToString("N"), Language = "auto", Tier = 1, Target = "../x" },
            new() { ItemId = good.Id.ToString("N"), Language = "auto", Tier = 1, Target = "de" },
        };

        var queue = new SubtitleQueueService();
        var (restored, _) = queue.RestoreFromEntries(pending, new List<QueueEntry>(), 3, Resolver(bad1, bad2, good));

        Assert.Equal(1, restored);
        Assert.Equal(1, queue.PriorityCount);
        Assert.True(queue.TryDequeuePriority(out var wi));
        Assert.Same(good, wi!.Item);
        Assert.Equal("de", wi.Target);
    }

    // ── Fan-out ─────────────────────────────────────────────────────────────

    [Fact]
    public void EnqueueTranslations_SeriesExpandsToOneJobPerEpisode()
    {
        var series = new Series { Id = Guid.NewGuid(), Name = "Show" };
        var episodes = Enumerable.Range(1, 3)
            .Select(i => (BaseItem)new Episode { Id = Guid.NewGuid(), Name = $"Episode {i}", Path = $"/media/show/e{i}.mkv" })
            .ToList();
        var library = new Mock<ILibraryManager>();
        library.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(episodes);

        var leaves = MediaItemResolver.ResolveLeafItems(series, enableLyrics: false, library.Object);
        Assert.Equal(3, leaves.Count);

        var queue = new SubtitleQueueService();
        Assert.True(queue.Enqueue(episodes[0], "auto", PriorityTier.High));   // a generate job already waiting

        Assert.Equal((3, 0), queue.EnqueueTranslations(leaves, "es", PriorityTier.High, force: false));
        Assert.Equal(4, queue.PriorityCount);
        Assert.Equal((0, 3), queue.EnqueueTranslations(leaves, "es", PriorityTier.High, force: false));

        var jobs = new List<SubtitleWorkItem>();
        while (queue.TryDequeuePriority(out var wi)) jobs.Add(wi!);
        var translate = jobs.Where(j => j.Target != null).ToList();
        Assert.Equal(3, translate.Count);
        Assert.All(translate, j =>
        {
            Assert.Equal("auto", j.Language);
            Assert.Equal("es", j.Target);
            Assert.False(j.Force);
        });
        Assert.Single(jobs, j => j.Target == null);
    }
}
