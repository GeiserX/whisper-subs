using System.Linq;
using WhisperSubs.Controller;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Issue #112: the multi-lane priority queue engine. These pin the ordering (strongest tier first,
/// FIFO within a tier), the de-dup + promotion behaviour, value merging, and the persistence snapshot
/// order — all on a Jellyfin-free <c>string</c> payload so the logic is fully unit-testable.
/// </summary>
public class PriorityLanesTests
{
    // Tier ints mirror PriorityTier: Critical=0 … Background=4 (lower = higher priority).
    private const int Critical = 0, High = 1, Medium = 2, Low = 3, Background = 4;

    [Fact]
    public void Enqueue_NewKey_ReturnsAdded_AndCounts()
    {
        var lanes = new PriorityLanes<string>();
        Assert.True(lanes.IsEmpty);

        Assert.Equal(LaneEnqueueOutcome.Added, lanes.Enqueue("a", Medium, "a"));

        Assert.False(lanes.IsEmpty);
        Assert.Equal(1, lanes.Count);
        Assert.True(lanes.ContainsKey("a"));
    }

    [Fact]
    public void Dequeue_ServesStrongestTierFirst()
    {
        var lanes = new PriorityLanes<string>();
        // Enqueue out of priority order.
        lanes.Enqueue("bg", Background, "bg");
        lanes.Enqueue("crit", Critical, "crit");
        lanes.Enqueue("med", Medium, "med");
        lanes.Enqueue("high", High, "high");

        Assert.True(lanes.TryDequeue(out var v1)); Assert.Equal("crit", v1);
        Assert.True(lanes.TryDequeue(out var v2)); Assert.Equal("high", v2);
        Assert.True(lanes.TryDequeue(out var v3)); Assert.Equal("med", v3);
        Assert.True(lanes.TryDequeue(out var v4)); Assert.Equal("bg", v4);
        Assert.False(lanes.TryDequeue(out _));
    }

    [Fact]
    public void Dequeue_IsFifoWithinSameTier()
    {
        var lanes = new PriorityLanes<string>();
        lanes.Enqueue("first", Medium, "first");
        lanes.Enqueue("second", Medium, "second");
        lanes.Enqueue("third", Medium, "third");

        Assert.True(lanes.TryDequeue(out var a)); Assert.Equal("first", a);
        Assert.True(lanes.TryDequeue(out var b)); Assert.Equal("second", b);
        Assert.True(lanes.TryDequeue(out var c)); Assert.Equal("third", c);
    }

    [Fact]
    public void TryDequeue_OnEmpty_ReturnsFalse()
    {
        var lanes = new PriorityLanes<string>();
        Assert.False(lanes.TryDequeue(out var v));
        Assert.Null(v);
    }

    [Fact]
    public void Enqueue_ExistingKey_StrongerTier_Promotes()
    {
        var lanes = new PriorityLanes<string>();
        lanes.Enqueue("x", Medium, "x");
        lanes.Enqueue("filler", Critical, "filler"); // sits ahead only because it's Critical

        // Re-enqueue x at Critical → it should promote and now sit BEHIND the earlier Critical filler.
        Assert.Equal(LaneEnqueueOutcome.Promoted, lanes.Enqueue("x", Critical, "x"));
        Assert.Equal(2, lanes.Count); // still de-duped, not two "x"

        Assert.True(lanes.TryDequeue(out var a)); Assert.Equal("filler", a);
        Assert.True(lanes.TryDequeue(out var b)); Assert.Equal("x", b);
    }

    [Fact]
    public void Enqueue_ExistingKey_WeakerOrEqualTier_IsDuplicate_AndKeepsPosition()
    {
        var lanes = new PriorityLanes<string>();
        lanes.Enqueue("x", High, "x");

        // A weaker (higher-number) tier must NOT demote or duplicate.
        Assert.Equal(LaneEnqueueOutcome.Duplicate, lanes.Enqueue("x", Background, "x"));
        Assert.Equal(LaneEnqueueOutcome.Duplicate, lanes.Enqueue("x", High, "x"));
        Assert.Equal(1, lanes.Count);

        // Still in the High lane (ahead of a genuinely Background item).
        lanes.Enqueue("bg", Background, "bg");
        Assert.True(lanes.TryDequeue(out var first)); Assert.Equal("x", first);
    }

    [Fact]
    public void Enqueue_MergeValue_IsApplied_OnDuplicateAndPromotion()
    {
        var lanes = new PriorityLanes<string>();
        lanes.Enqueue("x", Medium, "a", (existing, incoming) => existing + "+" + incoming);

        // Duplicate (weaker) still merges the value.
        lanes.Enqueue("x", Low, "b", (existing, incoming) => existing + "+" + incoming);
        // Promotion also merges the value.
        lanes.Enqueue("x", Critical, "c", (existing, incoming) => existing + "+" + incoming);

        Assert.True(lanes.TryDequeue(out var v));
        Assert.Equal("a+b+c", v);
    }

    [Fact]
    public void Snapshot_IsInDequeueOrder()
    {
        var lanes = new PriorityLanes<string>();
        lanes.Enqueue("m1", Medium, "m1");
        lanes.Enqueue("c1", Critical, "c1");
        lanes.Enqueue("m2", Medium, "m2");
        lanes.Enqueue("h1", High, "h1");

        var snap = lanes.Snapshot().Select(e => e.Key).ToList();
        Assert.Equal(new[] { "c1", "h1", "m1", "m2" }, snap);
        // Snapshot must not mutate the queue.
        Assert.Equal(4, lanes.Count);
    }

    [Fact]
    public void CountsByTier_ReportsPerLaneCounts()
    {
        var lanes = new PriorityLanes<string>();
        lanes.Enqueue("c1", Critical, "c1");
        lanes.Enqueue("m1", Medium, "m1");
        lanes.Enqueue("m2", Medium, "m2");

        var counts = lanes.CountsByTier();
        Assert.Equal(1, counts[Critical]);
        Assert.Equal(2, counts[Medium]);
        Assert.False(counts.ContainsKey(High)); // empty lanes are omitted
    }

    [Fact]
    public void Remove_DeletesKey_AndEmptyLaneIsCleaned()
    {
        var lanes = new PriorityLanes<string>();
        lanes.Enqueue("x", Medium, "x");
        Assert.True(lanes.Remove("x"));
        Assert.False(lanes.Remove("x"));   // gone
        Assert.Equal(0, lanes.Count);
        Assert.False(lanes.CountsByTier().ContainsKey(Medium)); // lane pruned
    }
}
