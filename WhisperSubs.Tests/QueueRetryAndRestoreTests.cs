using System.Text.Json;
using MediaBrowser.Controller.Entities;
using WhisperSubs.Controller;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// whisper-subs-1t0 — queue robustness: bounded auto-retry of killed/failed jobs and lossless restart via
/// persisted in-flight leases. Pins the pure retry decision (<see cref="SubtitleQueueService.ShouldRetry"/>),
/// the queue.json v1/v2 parse (<see cref="SubtitleQueueService.ParseQueueFile"/>), and the cancel/failure
/// re-enqueue transition (<see cref="SubtitleQueueService.RetryOrRelease"/>). Uses fresh service instances
/// (as <c>SubtitleQueueDedupTests</c> does) so each test starts clean; with no <c>Plugin.Instance</c> the
/// queue.json persistence is a disk-free no-op.
/// </summary>
public class QueueRetryAndRestoreTests
{
    private static Video NewItem(string name = "Film") => new Video { Id = Guid.NewGuid(), Name = name };

    // ── ShouldRetry truth table (retryCount 0..max, maxRetries incl. 0 and negative = disabled) ──

    [Theory]
    [InlineData(0, 0, false)]   // maxRetries 0 = auto-retry disabled = pre-feature "drop a killed job"
    [InlineData(1, 0, false)]
    [InlineData(0, -1, false)]  // any maxRetries <= 0 disables
    [InlineData(0, 1, true)]
    [InlineData(1, 1, false)]   // budget of 1 spent after the first retry
    [InlineData(0, 3, true)]
    [InlineData(1, 3, true)]
    [InlineData(2, 3, true)]
    [InlineData(3, 3, false)]   // 3 retries used → give up (4th attempt not taken)
    [InlineData(4, 3, false)]
    public void ShouldRetry_TruthTable(int retryCount, int maxRetries, bool expected)
        => Assert.Equal(expected, SubtitleQueueService.ShouldRetry(retryCount, maxRetries));

    // ── ParseQueueFile: v2 object shape, legacy v1 bare array, empty ──

    [Fact]
    public void ParseQueueFile_V2_SplitsPendingAndInFlight_PreservingTierForceRetry()
    {
        // A queue with BOTH pending and in-flight items survives a serialize/deserialize round-trip.
        var file = new QueueFile
        {
            Version = 2,
            Pending = new List<QueueEntry>
            {
                new QueueEntry { ItemId = "a", Language = "en", Force = false, Tier = (int)PriorityTier.High, RetryCount = 0 }
            },
            InFlight = new List<QueueEntry>
            {
                new QueueEntry { ItemId = "b", Language = "es", Force = true, Tier = (int)PriorityTier.Critical, RetryCount = 2 }
            }
        };
        var json = JsonSerializer.Serialize(file);

        var (pending, inFlight) = SubtitleQueueService.ParseQueueFile(json);

        Assert.Single(pending);
        Assert.Single(inFlight);
        Assert.Equal("a", pending[0].ItemId);
        Assert.Equal((int)PriorityTier.High, pending[0].Tier);

        // The in-flight lease comes back with its tier/force/retry intact so RestoreQueue can re-enqueue
        // it as Pending at the RIGHT tier (and keep its retry budget).
        Assert.Equal("b", inFlight[0].ItemId);
        Assert.Equal("es", inFlight[0].Language);
        Assert.True(inFlight[0].Force);
        Assert.Equal((int)PriorityTier.Critical, inFlight[0].Tier);
        Assert.Equal(2, inFlight[0].RetryCount);
    }

    [Fact]
    public void ParseQueueFile_V2_HandWrittenPascalCase_IsRead()
    {
        // Pins the actual on-disk field names the persister writes (default PascalCase serialization).
        var json = "{\"Version\":2," +
                   "\"Pending\":[{\"ItemId\":\"p\",\"Language\":\"en\",\"Force\":false,\"Tier\":1,\"RetryCount\":0}]," +
                   "\"InFlight\":[{\"ItemId\":\"f\",\"Language\":\"de\",\"Force\":true,\"Tier\":0,\"RetryCount\":1}]}";

        var (pending, inFlight) = SubtitleQueueService.ParseQueueFile(json);

        Assert.Single(pending);
        Assert.Single(inFlight);
        Assert.Equal("f", inFlight[0].ItemId);
        Assert.True(inFlight[0].Force);
        Assert.Equal(1, inFlight[0].RetryCount);
    }

    [Fact]
    public void ParseQueueFile_LegacyV1BareArray_AllPending_NoInFlight_TierlessNormalizesToHigh()
    {
        // A pre-feature queue.json is a bare array with no wrapper and no Tier / RetryCount fields.
        var json = "[{\"ItemId\":\"abc\",\"Language\":\"en\",\"Force\":true}]";

        var (pending, inFlight) = SubtitleQueueService.ParseQueueFile(json);

        Assert.Single(pending);
        Assert.Empty(inFlight);              // legacy has no in-flight concept
        Assert.Equal("abc", pending[0].ItemId);
        Assert.True(pending[0].Force);
        Assert.Null(pending[0].Tier);        // tier-less
        Assert.Equal(0, pending[0].RetryCount); // missing field → 0 (a fresh job)

        // …and the tier-less entry still normalizes to High (NOT Critical), preserving the #112 rule.
        Assert.Equal(PriorityTier.High, PriorityScheduling.NormalizeRestoredTier(pending[0].Tier));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t")]
    public void ParseQueueFile_EmptyOrWhitespace_YieldsTwoEmptyLists(string json)
    {
        var (pending, inFlight) = SubtitleQueueService.ParseQueueFile(json);
        Assert.Empty(pending);
        Assert.Empty(inFlight);
    }

    // ── RetryOrRelease: bounded re-enqueue on cancel/failure ──

    [Fact]
    public void RetryOrRelease_UnderMax_ReQueuesAtSameTier_WithIncrementedRetryCount()
    {
        var queue = new SubtitleQueueService();
        var item = NewItem();

        Assert.True(queue.Enqueue(item, "en", PriorityTier.High));   // fresh request: RetryCount 0
        Assert.True(queue.TryDequeuePriority(out var dispatched));   // now in-flight
        Assert.Equal(0, dispatched!.RetryCount);

        // Cancelled/failed with retry budget remaining → moved back to the lanes, dispatchable again.
        Assert.True(queue.RetryOrRelease(dispatched, maxRetries: 3));
        Assert.Equal(1, queue.PriorityCount);

        Assert.True(queue.TryDequeuePriority(out var again));
        Assert.Same(item, again!.Item);
        Assert.Equal(PriorityTier.High, again.Tier);   // original tier preserved
        Assert.Equal(1, again.RetryCount);             // incremented
    }

    [Fact]
    public void RetryOrRelease_AtMax_DropsInsteadOfReQueue_AndReleasesIdentity()
    {
        var queue = new SubtitleQueueService();
        var item = NewItem();
        var key = SubtitleQueueService.IdentityKey(item.Id, "en");

        // An item that has already used its whole retry budget (RetryCount == maxRetries).
        var wi = new SubtitleWorkItem { Item = item, Language = "en", Tier = PriorityTier.High, RetryCount = 3 };
        Assert.True(queue.TryReserve(key, wi));   // in-flight

        Assert.False(queue.RetryOrRelease(wi, maxRetries: 3));   // give up
        Assert.Equal(0, queue.PriorityCount);                    // NOT re-queued

        // Identity was released → a brand-new request for it can be reserved again (no leak).
        Assert.True(queue.TryReserve(key, wi));
    }

    [Fact]
    public void RetryOrRelease_MaxRetriesZero_NeverReQueues_IsOptOut()
    {
        var queue = new SubtitleQueueService();
        var item = NewItem();

        Assert.True(queue.Enqueue(item, "en", PriorityTier.Medium));
        Assert.True(queue.TryDequeuePriority(out var dispatched));

        // 0 = auto-retry disabled = the pre-feature "a killed/failed job is dropped" behaviour.
        Assert.False(queue.RetryOrRelease(dispatched!, maxRetries: 0));
        Assert.Equal(0, queue.PriorityCount);
    }

    [Fact]
    public void RetryOrRelease_ThenConcurrentReRequest_Merges_AndKeepsLargerRetryCount()
    {
        var queue = new SubtitleQueueService();
        var item = NewItem();

        Assert.True(queue.Enqueue(item, "en", PriorityTier.Medium));    // RetryCount 0
        Assert.True(queue.TryDequeuePriority(out var dispatched));      // in-flight
        Assert.True(queue.RetryOrRelease(dispatched!, maxRetries: 3));  // re-queued at RetryCount 1

        // A fresh admin request for the SAME identity while it's queued must merge (not duplicate) and must
        // NOT reset the retry budget — Force OR'd, tier promoted, retry count kept at the larger value.
        Assert.False(queue.Enqueue(item, "en", PriorityTier.Critical, force: true));  // merged, not added
        Assert.Equal(1, queue.PriorityCount);

        Assert.True(queue.TryDequeuePriority(out var merged));
        Assert.True(merged!.Force);
        Assert.Equal(PriorityTier.Critical, merged.Tier);
        Assert.Equal(1, merged.RetryCount);   // NOT reset to 0 by the fresh request
    }

    [Fact]
    public void RetryOrRelease_ReQueuedItem_IsInFlightXorQueued_AtEachStep()
    {
        // The reverse transition must never leave the identity both in-flight AND queued.
        var queue = new SubtitleQueueService();
        var item = NewItem();
        var key = SubtitleQueueService.IdentityKey(item.Id, "en");

        Assert.True(queue.Enqueue(item, "en", PriorityTier.High));
        Assert.True(queue.TryDequeuePriority(out var dispatched));
        // In-flight now → a re-request is rejected (reservation held).
        Assert.False(queue.TryReserve(key));

        Assert.True(queue.RetryOrRelease(dispatched!, maxRetries: 3));
        // Back in the lanes now → NOT in-flight, so the identity is reservable again (would be re-dequeued).
        Assert.Equal(1, queue.PriorityCount);
        Assert.True(queue.TryReserve(key));   // proves it left the in-flight set
    }

    // ── Bounded in-flight restore (whisper-subs-1t0 Fix 1 — the hard-kill boot-loop guard) ──

    private static Func<Guid, BaseItem?> Resolver(params BaseItem[] items)
    {
        var byId = items.ToDictionary(i => i.Id);
        return id => byId.GetValueOrDefault(id);
    }

    private static QueueEntry NewEntry(BaseItem item, int retryCount, string language = "en",
        PriorityTier tier = PriorityTier.High, bool force = false)
        => new QueueEntry
        {
            ItemId = item.Id.ToString("N"),
            Language = language,
            Force = force,
            Tier = (int)tier,
            RetryCount = retryCount
        };

    [Theory]
    // Pending (wasInFlight=false) is ALWAYS restored, whatever its count — it never started.
    [InlineData(false, 0, 3, true)]
    [InlineData(false, 3, 3, true)]
    [InlineData(false, 99, 3, true)]
    // In-flight (wasInFlight=true) restores only while strictly under the cap (one attempt already consumed).
    [InlineData(true, 0, 3, true)]
    [InlineData(true, 2, 3, true)]    // maxRetries-1 → still restored (at +1)
    [InlineData(true, 3, 3, false)]   // at the cap → dropped
    [InlineData(true, 4, 3, false)]   // over the cap → dropped
    [InlineData(true, 0, 0, false)]   // retry disabled → a killed in-flight lease is dropped
    public void ShouldRestore_TruthTable(bool wasInFlight, int retryCount, int maxRetries, bool expected)
        => Assert.Equal(expected, SubtitleQueueService.ShouldRestore(wasInFlight, retryCount, maxRetries));

    [Theory]
    [InlineData(false, 0, 0)]   // Pending: unchanged (never started)
    [InlineData(false, 2, 2)]   // Pending: unchanged
    [InlineData(true, 0, 1)]    // In-flight: +1 (one attempt consumed)
    [InlineData(true, 2, 3)]    // In-flight: +1
    public void RestoredRetryCount_PendingUnchanged_InFlightPlusOne(bool wasInFlight, int retryCount, int expected)
        => Assert.Equal(expected, SubtitleQueueService.RestoredRetryCount(wasInFlight, retryCount));

    [Fact]
    public void RestoreFromEntries_InFlightConsumesOneAttempt_PendingUnchanged_OverCapDropped()
    {
        // The highest-value test — the exact case Fix 1 exists for: an in-flight lease at the cap must NOT be
        // restored (else it OOM-kills, restores, OOM-kills … a boot-loop that wedges ALL generation), one under
        // the cap comes back at +1 (its kill counted as an attempt), and a Pending item keeps its count.
        const int maxRetries = 3;
        var queue = new SubtitleQueueService();

        var pending = NewItem("Pending");                 // Pending at rc=2 → restored at 2 (unchanged)
        var inflightBudget = NewItem("InFlightBudget");   // In-flight at rc=2 (=max-1) → restored at 3
        var inflightAtCap = NewItem("InFlightAtCap");     // In-flight at rc=3 (=max)  → DROPPED, not restored

        var (restored, givenUp) = queue.RestoreFromEntries(
            pending: new List<QueueEntry> { NewEntry(pending, retryCount: 2) },
            inFlight: new List<QueueEntry>
            {
                NewEntry(inflightBudget, retryCount: maxRetries - 1),
                NewEntry(inflightAtCap, retryCount: maxRetries)
            },
            maxRetries: maxRetries,
            resolveItem: Resolver(pending, inflightBudget, inflightAtCap));

        Assert.Equal(2, restored);                          // pending + inflightBudget; inflightAtCap dropped
        Assert.Single(givenUp);
        Assert.Equal("InFlightAtCap", givenUp[0].Name);
        Assert.Equal(maxRetries, givenUp[0].RetryCount);    // reported at its prior (spent) count
        Assert.Equal(2, queue.PriorityCount);

        // Drain and confirm the restored RetryCounts.
        var counts = new Dictionary<string, int>();
        while (queue.TryDequeuePriority(out var wi) && wi != null)
            counts[wi.Item.Name] = wi.RetryCount;

        Assert.Equal(2, counts["Pending"]);                 // Pending: unchanged (never started)
        Assert.Equal(maxRetries, counts["InFlightBudget"]); // In-flight: RetryCount+1 (one attempt consumed)
        Assert.False(counts.ContainsKey("InFlightAtCap"));  // dropped — never re-enters the lanes
    }

    [Fact]
    public void RestoreFromEntries_SameIdentityInPendingAndInFlight_CollapsesToOneLaneEntry()
    {
        // Pending∩InFlight overlap (should-not-happen, but be safe): the same identity in BOTH lists restores
        // as ONE lane entry (merge, not double). Pending is processed first; the in-flight copy (rc+1) merges
        // onto it via the lane dedup, and MergeWork keeps the larger retry count.
        var queue = new SubtitleQueueService();
        var item = NewItem("Overlap");

        var (restored, givenUp) = queue.RestoreFromEntries(
            pending: new List<QueueEntry> { NewEntry(item, retryCount: 0) },
            inFlight: new List<QueueEntry> { NewEntry(item, retryCount: 0) },
            maxRetries: 3,
            resolveItem: Resolver(item));

        Assert.Equal(1, restored);                 // ONE lane entry, not two
        Assert.Empty(givenUp);
        Assert.Equal(1, queue.PriorityCount);

        Assert.True(queue.TryDequeuePriority(out var wi));
        Assert.Same(item, wi!.Item);
        Assert.Equal(1, wi.RetryCount);            // in-flight copy's rc+1 (=1) wins the merge over pending's 0
    }

    [Fact]
    public void RestoreFromEntries_SkipsUnparseableGuidAndMissingItem_RestoresTheRest()
    {
        var queue = new SubtitleQueueService();
        var present = NewItem("Present");
        var missing = NewItem("Missing");   // deliberately NOT known to the resolver

        var (restored, givenUp) = queue.RestoreFromEntries(
            pending: new List<QueueEntry>
            {
                new QueueEntry { ItemId = "not-a-guid", Language = "en", Tier = (int)PriorityTier.High },
                NewEntry(missing, retryCount: 0),   // resolver returns null → skipped
                NewEntry(present, retryCount: 0)    // restored
            },
            inFlight: new List<QueueEntry>(),
            maxRetries: 3,
            resolveItem: Resolver(present));        // only 'present' resolves

        Assert.Equal(1, restored);
        Assert.Empty(givenUp);
        Assert.Equal(1, queue.PriorityCount);
    }

    [Fact]
    public void RestoreFromEntries_TierlessInFlightLease_NormalizesToHigh_AndRestoresAtPlusOne()
    {
        // A legacy tier-less in-flight entry (Tier null) still normalizes to High (NOT Critical) AND obeys the
        // in-flight +1 rule — the #112 migration rule and the 1t0 budget rule compose correctly.
        var queue = new SubtitleQueueService();
        var item = NewItem("Legacy");

        var (restored, givenUp) = queue.RestoreFromEntries(
            pending: new List<QueueEntry>(),
            inFlight: new List<QueueEntry>
            {
                new QueueEntry { ItemId = item.Id.ToString("N"), Language = "en", Tier = null, RetryCount = 0 }
            },
            maxRetries: 3,
            resolveItem: Resolver(item));

        Assert.Equal(1, restored);
        Assert.Empty(givenUp);
        Assert.True(queue.TryDequeuePriority(out var wi));
        Assert.Equal(PriorityTier.High, wi!.Tier);   // tier-less → High
        Assert.Equal(1, wi.RetryCount);              // in-flight → +1
    }
}
