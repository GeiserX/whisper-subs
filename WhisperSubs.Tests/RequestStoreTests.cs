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

    // ── Translation requests (per-title) ──────────────────────────────────

    private static RequestCreateResult CreateTranslate(
        SubtitleRequestStore store, string user, string item, string? target, int active = 3, int daily = 5)
        => store.TryCreate(item, "Name", "Movie", "auto", user, "user-" + user, PriorityTier.Medium, 1, false,
            Now, Window, daily, active, 500, target);

    [Fact]
    public void TranslateAndGenerate_SameItem_AreSeparateRequests_SameTargetIsADuplicate()
    {
        var store = new SubtitleRequestStore();
        Assert.Equal(RequestCreateOutcome.Created, CreateTranslate(store, "alice", "item1", null).Outcome);
        var es = CreateTranslate(store, "alice", "item1", "es");
        Assert.Equal(RequestCreateOutcome.Created, es.Outcome);
        Assert.Equal("es", es.Request!.Target);

        var again = CreateTranslate(store, "alice", "item1", "ES");
        Assert.Equal(RequestCreateOutcome.DuplicateActive, again.Outcome);
        Assert.Same(es.Request, again.Request);

        Assert.Equal(RequestCreateOutcome.Created, CreateTranslate(store, "alice", "item1", "nl").Outcome);
    }

    [Fact]
    public void TranslateRequests_CountTowardTheActiveCapAndQuota()
    {
        var store = new SubtitleRequestStore();
        Assert.Equal(RequestCreateOutcome.Created, CreateTranslate(store, "alice", "item1", null, active: 2).Outcome);
        Assert.Equal(RequestCreateOutcome.Created, CreateTranslate(store, "alice", "item1", "es", active: 2).Outcome);
        Assert.Equal(RequestCreateOutcome.ActiveCapExceeded, CreateTranslate(store, "alice", "item1", "nl", active: 2).Outcome);

        var quota = new SubtitleRequestStore();
        Assert.Equal(RequestCreateOutcome.Created, CreateTranslate(quota, "bob", "item1", "es", active: 0, daily: 1).Outcome);
        Assert.Equal(RequestCreateOutcome.QuotaExceeded, CreateTranslate(quota, "bob", "item2", "de", active: 0, daily: 1).Outcome);
    }

    [Fact]
    public void RequestWithoutTarget_StoresNullTarget_AndDedupsAsBefore()
    {
        var store = new SubtitleRequestStore();
        var first = Create(store, "alice", "item1");
        Assert.Null(first.Request!.Target);
        Assert.Equal(RequestCreateOutcome.DuplicateActive, Create(store, "alice", "item1").Outcome);
    }

    [Fact]
    public void RequestWithNullTarget_SerializesWithoutATargetField()
    {
        var store = new SubtitleRequestStore();
        var plain = Create(store, "alice", "item1").Request!;
        Assert.DoesNotContain("\"Target\"", System.Text.Json.JsonSerializer.Serialize(plain));

        var translate = CreateTranslate(store, "alice", "item2", "es").Request!;
        Assert.Contains("\"Target\":\"es\"", System.Text.Json.JsonSerializer.Serialize(translate));
    }
    // requests.json is read from disk and an approved translation request's target ends up in a file name,
    // so it is re-checked on restore the way queue.json's is. A legacy request (no Target) is unchanged.
    [Fact]
    public void Restore_ReChecksTheTargetOfEveryTranslationRequest()
    {
        const string json = """
            [
              { "Id": "a", "ItemId": "i1", "Language": "auto", "State": "Pending", "Target": "../x" },
              { "Id": "b", "ItemId": "i2", "Language": "auto", "State": "Pending", "Target": "ES" },
              { "Id": "c", "ItemId": "i3", "Language": "auto", "State": "Pending" },
              { "Id": "d", "ItemId": "i4", "Language": "auto", "State": "Pending", "Target": " " },
              null
            ]
            """;
        var loaded = System.Text.Json.JsonSerializer.Deserialize<List<SubtitleRequest?>>(json)!;

        var kept = SubtitleRequestStore.KeepRestorable(loaded);

        Assert.Equal(new[] { "b", "c" }, kept.Select(r => r.Id));
        Assert.Equal("es", kept[0].Target);
        Assert.Null(kept[1].Target);
    }
}
