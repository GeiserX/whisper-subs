using System;
using System.Linq;
using WhisperSubs.Controller;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Issue #112: the user-request store is the single security-critical chokepoint that enforces de-dup,
/// global cap, per-user active cap and rolling quota before any CPU is committed. Each test uses a fresh
/// store instance (no Plugin.Instance → no disk) to pin that in-memory decision logic.
/// </summary>
public class RequestStoreTests
{
    private static readonly long Window = TimeSpan.FromHours(24).Ticks;
    private const long Now = 638_000_000_000_000_000L;

    private static RequestCreateResult Create(
        SubtitleRequestStore store, string user, string item,
        bool auto = false, long? now = null, int daily = 5, int active = 3, int global = 500, int count = 1)
        => store.TryCreate(item, "Name", "Movie", "en", user, "user-" + user, PriorityTier.Medium, count, auto,
            now ?? Now, Window, daily, active, global);

    [Fact]
    public void Create_UnderLimits_ReturnsCreated_Pending()
    {
        var store = new SubtitleRequestStore();
        var r = Create(store, "alice", "item1");
        Assert.Equal(RequestCreateOutcome.Created, r.Outcome);
        Assert.NotNull(r.Request);
        Assert.Equal(RequestState.Pending, r.Request!.State);
        Assert.Equal(1, store.CountActive());
    }

    [Fact]
    public void Create_AutoApprove_ReturnsQueued()
    {
        var store = new SubtitleRequestStore();
        var r = Create(store, "alice", "item1", auto: true);
        Assert.Equal(RequestCreateOutcome.Created, r.Outcome);
        Assert.Equal(RequestState.Queued, r.Request!.State);
    }

    [Fact]
    public void Create_DuplicateActive_SameUserAndItem_ReturnsExisting()
    {
        var store = new SubtitleRequestStore();
        var first = Create(store, "alice", "item1");
        var second = Create(store, "alice", "item1");

        Assert.Equal(RequestCreateOutcome.DuplicateActive, second.Outcome);
        Assert.Same(first.Request, second.Request);
        Assert.Equal(1, store.CountActive()); // not double-counted
    }

    [Fact]
    public void Create_AfterTerminal_SameItem_IsAllowedAgain()
    {
        var store = new SubtitleRequestStore();
        var first = Create(store, "alice", "item1");
        store.SetState(first.Request!.Id, RequestState.Declined, Now);

        var second = Create(store, "alice", "item1");
        Assert.Equal(RequestCreateOutcome.Created, second.Outcome);
        Assert.NotEqual(first.Request!.Id, second.Request!.Id);
    }

    [Fact]
    public void Create_ExceedingDailyQuota_IsDenied()
    {
        var store = new SubtitleRequestStore();
        Create(store, "alice", "a", daily: 2, active: 10);
        Create(store, "alice", "b", daily: 2, active: 10);
        var third = Create(store, "alice", "c", daily: 2, active: 10);

        Assert.Equal(RequestCreateOutcome.QuotaExceeded, third.Outcome);
        Assert.Null(third.Request);
    }

    [Fact]
    public void Create_ExceedingActiveCap_IsDenied()
    {
        var store = new SubtitleRequestStore();
        Create(store, "alice", "a", daily: 10, active: 2);
        Create(store, "alice", "b", daily: 10, active: 2);
        var third = Create(store, "alice", "c", daily: 10, active: 2);

        Assert.Equal(RequestCreateOutcome.ActiveCapExceeded, third.Outcome);
    }

    [Fact]
    public void Create_ExceedingGlobalCap_IsDenied()
    {
        var store = new SubtitleRequestStore();
        Create(store, "alice", "a", global: 1);
        var other = Create(store, "bob", "b", global: 1);

        Assert.Equal(RequestCreateOutcome.GlobalCapExceeded, other.Outcome);
    }

    [Fact]
    public void Create_ZeroLimits_MeanUnlimited()
    {
        var store = new SubtitleRequestStore();
        for (int i = 0; i < 20; i++)
        {
            var r = Create(store, "alice", "item" + i, daily: 0, active: 0, global: 0);
            Assert.Equal(RequestCreateOutcome.Created, r.Outcome);
        }
    }

    [Fact]
    public void Quota_ResetsAfterWindow()
    {
        var store = new SubtitleRequestStore();
        // Two requests 30h ago (outside the 24h window) must not count toward today's quota.
        Create(store, "alice", "old1", now: Now - TimeSpan.FromHours(30).Ticks, daily: 2, active: 10);
        Create(store, "alice", "old2", now: Now - TimeSpan.FromHours(29).Ticks, daily: 2, active: 10);
        var fresh = Create(store, "alice", "new", daily: 2, active: 10);

        Assert.Equal(RequestCreateOutcome.Created, fresh.Outcome);
    }

    [Fact]
    public void SetState_UpdatesState_AndUnknownIdReturnsNull()
    {
        var store = new SubtitleRequestStore();
        var r = Create(store, "alice", "item1");
        var updated = store.SetState(r.Request!.Id, RequestState.Queued, Now + 1);

        Assert.NotNull(updated);
        Assert.Equal(RequestState.Queued, updated!.State);
        Assert.Null(store.SetState("nope", RequestState.Completed, Now));
    }

    [Fact]
    public void GetByUser_ReturnsOnlyThatUsersRequests()
    {
        var store = new SubtitleRequestStore();
        Create(store, "alice", "a");
        Create(store, "bob", "b");

        var aliceReqs = store.GetByUser("alice");
        Assert.Single(aliceReqs);
        Assert.All(aliceReqs, r => Assert.Equal("alice", r.UserId));
    }

    [Fact]
    public void CountActiveForUser_CountsPendingAndQueued_NotTerminal()
    {
        var store = new SubtitleRequestStore();
        var a = Create(store, "alice", "a");            // Pending
        var b = Create(store, "alice", "b");            // Pending
        store.SetState(b.Request!.Id, RequestState.Queued, Now);   // Queued (still active)
        var c = Create(store, "alice", "c");
        store.SetState(c.Request!.Id, RequestState.Completed, Now); // terminal

        Assert.Equal(2, store.CountActiveForUser("alice"));
    }

    [Fact]
    public void GetById_FindsExisting_AndReturnsNullForUnknown()
    {
        var store = new SubtitleRequestStore();
        var r = Create(store, "alice", "item1");
        Assert.Same(r.Request, store.GetById(r.Request!.Id));
        Assert.Null(store.GetById("does-not-exist"));
    }

    [Fact]
    public void TryTransition_OnlySwapsFromTheExpectedState()
    {
        var store = new SubtitleRequestStore();
        var r = Create(store, "alice", "item1"); // Pending
        var id = r.Request!.Id;

        // Wrong expected state → no-op, request unchanged.
        Assert.False(store.TryTransition(id, RequestState.Queued, RequestState.Completed, Now, out var same));
        Assert.Equal(RequestState.Pending, same!.State);

        // Correct expected → transitions.
        Assert.True(store.TryTransition(id, RequestState.Pending, RequestState.Queued, Now, out var moved));
        Assert.Equal(RequestState.Queued, moved!.State);

        // A second attempt from the (now stale) expected state must fail — the CAS that prevents a double-approve.
        Assert.False(store.TryTransition(id, RequestState.Pending, RequestState.Queued, Now, out _));

        // Unknown id → false, null request.
        Assert.False(store.TryTransition("nope", RequestState.Pending, RequestState.Queued, Now, out var missing));
        Assert.Null(missing);
    }

    [Fact]
    public void GetAll_ReturnsEveryRequest_NewestFirst()
    {
        var store = new SubtitleRequestStore();
        Create(store, "alice", "a", now: Now - TimeSpan.FromHours(2).Ticks);
        Create(store, "bob", "b", now: Now); // newer
        var all = store.GetAll();

        Assert.Equal(2, all.Count);
        Assert.True(all[0].CreatedTicks >= all[1].CreatedTicks); // newest first
    }
}
