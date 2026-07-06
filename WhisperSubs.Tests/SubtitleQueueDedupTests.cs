using MediaBrowser.Controller.Entities;
using WhisperSubs.Controller;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Pins the v4.0 dispatcher's core safety invariant: one (item, language) identity is never
/// dispatchable twice concurrently. <c>Enqueue</c> and <c>TryDequeuePriority</c> coordinate under one
/// dispatch gate — a dequeue atomically moves the identity from the lanes into the in-flight set, a
/// re-enqueue while QUEUED merges onto the existing entry (Force OR'd, tier promoted), and a
/// re-enqueue while IN-FLIGHT is rejected until the dispatcher releases the reservation. Uses fresh
/// service instances (the singleton is convention, not enforced) so each test starts with empty
/// lanes/in-flight state; with no <c>Plugin.Instance</c> the queue.json persistence is a no-op, so
/// these stay disk-free — the same pattern as <c>RequestStoreTests</c>.
/// </summary>
public class SubtitleQueueDedupTests
{
    private static Video NewItem(string name = "Film") => new Video { Id = Guid.NewGuid(), Name = name };

    [Fact]
    public void Dequeue_MovesIdentityInFlight_SoReEnqueueCannotDispatchTwice()
    {
        var queue = new SubtitleQueueService();
        var item = NewItem();

        Assert.True(queue.Enqueue(item, "en", PriorityTier.High));
        Assert.True(queue.TryDequeuePriority(out var dispatched));
        Assert.Same(item, dispatched!.Item);

        // The dequeue reserved the identity in-flight (queued XOR in-flight holds)...
        Assert.False(queue.TryReserve(SubtitleQueueService.IdentityKey(item.Id, "en")));
        Assert.Equal(0, queue.PriorityCount);

        // ...so a re-request mid-transcription must not create a second dispatchable entry,
        // no matter how strong the tier or the force flag.
        Assert.False(queue.Enqueue(item, "en", PriorityTier.Critical, force: true));
        Assert.Equal(0, queue.PriorityCount);
        Assert.False(queue.TryDequeuePriority(out var second));
        Assert.Null(second);
    }

    [Fact]
    public void Enqueue_WhileInFlight_LanguageCaseDiffers_StillRejected()
    {
        // IdentityKey lowercases the language, so "EN" and "en" are the same unit of work across
        // BOTH the queued and in-flight sets — a case-variant re-request must not double-dispatch.
        var queue = new SubtitleQueueService();
        var item = NewItem();

        Assert.True(queue.Enqueue(item, "en", PriorityTier.High));
        Assert.True(queue.TryDequeuePriority(out _));

        Assert.False(queue.Enqueue(item, "EN", PriorityTier.Critical));
        Assert.False(queue.TryDequeuePriority(out _));
    }

    [Fact]
    public void Release_MakesTheSameIdentityDispatchableAgain()
    {
        var queue = new SubtitleQueueService();
        var item = NewItem();

        Assert.True(queue.Enqueue(item, "en", PriorityTier.High));
        Assert.True(queue.TryDequeuePriority(out _));

        // The dispatcher's per-job finally releases the reservation once transcription ends.
        queue.Release(SubtitleQueueService.IdentityKey(item.Id, "en"));

        // The identity is now requestable and dispatchable again (e.g. a forced regenerate).
        Assert.True(queue.Enqueue(item, "en", PriorityTier.High));
        Assert.True(queue.TryDequeuePriority(out var again));
        Assert.Same(item, again!.Item);
    }

    [Fact]
    public void ReEnqueue_WhileQueued_MergesForceAndTier_InsteadOfDuplicating()
    {
        var queue = new SubtitleQueueService();
        var item = NewItem();

        // A user request at Medium followed by an admin forced request at Critical collapses to ONE
        // entry carrying the OR'd force and the promoted tier — never two competing jobs (#112).
        Assert.True(queue.Enqueue(item, "en", PriorityTier.Medium, force: false));
        Assert.False(queue.Enqueue(item, "en", PriorityTier.Critical, force: true)); // merged, not added
        Assert.Equal(1, queue.PriorityCount);

        Assert.True(queue.TryDequeuePriority(out var merged));
        Assert.True(merged!.Force);
        Assert.Equal(PriorityTier.Critical, merged.Tier);
        Assert.Same(item, merged.Item);

        Assert.False(queue.TryDequeuePriority(out _)); // there was only ever one entry
    }

    [Fact]
    public void ReEnqueue_WithWeakerTier_NeverWeakensTheQueuedEntry()
    {
        var queue = new SubtitleQueueService();
        var item = NewItem();

        Assert.True(queue.Enqueue(item, "en", PriorityTier.Critical, force: true));
        Assert.False(queue.Enqueue(item, "en", PriorityTier.Background, force: false));
        Assert.Equal(1, queue.PriorityCount);

        Assert.True(queue.TryDequeuePriority(out var kept));
        Assert.True(kept!.Force);                        // force is OR'd, never cleared
        Assert.Equal(PriorityTier.Critical, kept.Tier);  // tier only ever promotes
    }
}
