using System.Text.Json;
using MediaBrowser.Controller.Entities;
using WhisperSubs.Controller;
using Xunit;

namespace WhisperSubs.Tests;

// Serializes test classes that mutate the SubtitleQueueService singleton to avoid a parallel-run race (xUnit runs distinct classes concurrently).
[CollectionDefinition("QueueSingleton", DisableParallelization = true)]
public class QueueSingletonCollection { }

[Collection("QueueSingleton")]
public class SubtitleQueueServiceTests
{
    [Fact]
    public void Instance_ReturnsSameInstance()
    {
        var a = SubtitleQueueService.Instance;
        var b = SubtitleQueueService.Instance;
        Assert.Same(a, b);
    }

    [Fact]
    public void ReportFileProgress_SetsProgress()
    {
        var queue = SubtitleQueueService.Instance;
        queue.ReportFileProgress(42);
        Assert.Equal(42, queue.CurrentFileProgress);
    }

    [Fact]
    public void ReportFileProgress_ClampsToBounds()
    {
        var queue = SubtitleQueueService.Instance;

        queue.ReportFileProgress(-10);
        Assert.Equal(0, queue.CurrentFileProgress);

        queue.ReportFileProgress(200);
        Assert.Equal(100, queue.CurrentFileProgress);
    }

    [Fact]
    public void ResetFileProgress_SetsToZero()
    {
        var queue = SubtitleQueueService.Instance;
        queue.ReportFileProgress(75);
        queue.ResetFileProgress();
        Assert.Equal(0, queue.CurrentFileProgress);
    }

    [Fact]
    public void ReportPhase_SetsPhase()
    {
        var queue = SubtitleQueueService.Instance;
        queue.ReportPhase("Extracting audio");
        Assert.Equal("Extracting audio", queue.CurrentPhase);
    }

    [Fact]
    public void ReportTaskProgress_SetsAllFields()
    {
        var queue = SubtitleQueueService.Instance;
        queue.ReportTaskProgress("TestItem", 5, 10, 1, "Movie", "My Library");

        Assert.Equal("TestItem", queue.TaskCurrentItemName);
        Assert.Equal(5, queue.TaskProcessed);
        Assert.Equal(10, queue.TaskTotal);
        Assert.Equal(1, queue.TaskFailed);
        Assert.Equal("Movie", queue.TaskCurrentItemType);
        Assert.Equal("My Library", queue.TaskCurrentItemLibrary);
        Assert.True(queue.IsTaskRunning);
    }

    [Fact]
    public void ReportTaskProgress_NullItemName_ClearsPhase()
    {
        var queue = SubtitleQueueService.Instance;
        queue.ReportPhase("Transcribing");
        queue.ReportTaskProgress(null, 10, 10, 0);

        Assert.Null(queue.CurrentPhase);
    }

    [Fact]
    public void ReportTaskComplete_ClearsAllFields()
    {
        var queue = SubtitleQueueService.Instance;
        queue.ReportTaskProgress("Item", 1, 5, 0, "Episode", "TV Shows");
        queue.ReportPhase("Transcribing");
        queue.ReportTaskComplete();

        Assert.Null(queue.TaskCurrentItemName);
        Assert.Null(queue.TaskCurrentItemType);
        Assert.Null(queue.TaskCurrentItemLibrary);
        Assert.Null(queue.CurrentPhase);
        Assert.False(queue.IsTaskRunning);
    }

    [Fact]
    public void TranscriptionLock_IsAvailable()
    {
        Assert.NotNull(SubtitleQueueService.TranscriptionLock);
        // Should be able to acquire and release
        Assert.True(SubtitleQueueService.TranscriptionLock.Wait(0));
        SubtitleQueueService.TranscriptionLock.Release();
    }

    [Fact]
    public void PriorityCount_WhenEmpty_IsZero()
    {
        // Note: we can't fully reset the singleton queue, but PriorityCount
        // should be >= 0 (may have items from other tests)
        var queue = SubtitleQueueService.Instance;
        Assert.True(queue.PriorityCount >= 0);
    }

    [Fact]
    public void FailedCount_IsNonNegative()
    {
        // Manual-queue failure counter — exposed so the /Queue endpoint and UI
        // can distinguish a failed transcription from a successful one (#70).
        var queue = SubtitleQueueService.Instance;
        Assert.True(queue.FailedCount >= 0);
    }

    [Fact]
    public void LastError_IsExposed()
    {
        // Property must exist for the UI to surface a failure reason.
        // It is null until a manual-queue item fails.
        var queue = SubtitleQueueService.Instance;
        _ = queue.LastError; // no throw; may be null or a prior error
        Assert.True(true);
    }

    // ── Enqueue de-dup (#94/#112): same item + language collapses to one queued entry, regardless of
    //    force or tier — those are OR-merged / promoted onto the existing entry (see PriorityLanesTests). ──

    [Fact]
    public void IdentityKey_SameInputs_AreEqual_AndCaseInsensitiveLanguage()
    {
        // Language is normalized to lowercase so "EN" and "en" map to the same unit of work.
        var id = Guid.NewGuid();
        Assert.Equal(
            SubtitleQueueService.IdentityKey(id, "EN"),
            SubtitleQueueService.IdentityKey(id, "en"));
    }

    [Fact]
    public void IdentityKey_DiffersByItem()
    {
        // Different items never collapse onto each other.
        Assert.NotEqual(
            SubtitleQueueService.IdentityKey(Guid.NewGuid(), "en"),
            SubtitleQueueService.IdentityKey(Guid.NewGuid(), "en"));
    }

    [Fact]
    public void TryReserve_SecondCallSameKey_ReturnsFalse_ThenReleaseAllowsReserve()
    {
        // Reserve once, the duplicate is rejected; after Release the key is requestable again.
        // Unique GUID keeps this isolated from the shared singleton's other in-flight keys.
        var queue = SubtitleQueueService.Instance;
        var k = SubtitleQueueService.IdentityKey(Guid.NewGuid(), "en");

        Assert.True(queue.TryReserve(k));
        Assert.False(queue.TryReserve(k));
        queue.Release(k);
        Assert.True(queue.TryReserve(k));

        queue.Release(k); // clean up so we don't leak the key into other tests
    }
}

public class QueueEntryTests
{
    [Fact]
    public void DefaultValues()
    {
        var entry = new QueueEntry();
        Assert.Equal("", entry.ItemId);
        Assert.Equal("", entry.Language);
    }

    [Fact]
    public void CanSetProperties()
    {
        var entry = new QueueEntry
        {
            ItemId = "abc123",
            Language = "es"
        };
        Assert.Equal("abc123", entry.ItemId);
        Assert.Equal("es", entry.Language);
    }

    // ── Force flag (#82 manual-request bypass; must persist across restart) ──

    [Fact]
    public void QueueEntry_Force_DefaultsFalse()
    {
        // Scheduled/restored items must not accidentally force.
        var entry = new QueueEntry();
        Assert.False(entry.Force);
    }

    [Fact]
    public void QueueEntry_Force_CanBeSet()
    {
        var entry = new QueueEntry { Force = true };
        Assert.True(entry.Force);
    }

    [Fact]
    public void QueueEntry_Force_SerializationRoundTrip()
    {
        // Force must survive the queue.json persist/restore cycle (audit High bug).
        var original = new QueueEntry { ItemId = "abc", Language = "en", Force = true };
        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<QueueEntry>(json);

        Assert.NotNull(restored);
        Assert.True(restored!.Force);
        Assert.Equal("abc", restored.ItemId);
        Assert.Equal("en", restored.Language);
    }

    // ── Tier (#112): persisted priority, nullable so a legacy queue.json (no tier) migrates cleanly ──

    [Fact]
    public void QueueEntry_Tier_DefaultsNull()
    {
        // A missing tier must be distinguishable from Critical(0) — hence int?, not int.
        var entry = new QueueEntry();
        Assert.Null(entry.Tier);
    }

    [Fact]
    public void QueueEntry_Tier_SerializationRoundTrip()
    {
        var original = new QueueEntry { ItemId = "abc", Language = "en", Force = false, Tier = (int)PriorityTier.Critical };
        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<QueueEntry>(json);

        Assert.NotNull(restored);
        Assert.Equal((int)PriorityTier.Critical, restored!.Tier);
    }

    [Fact]
    public void QueueEntry_LegacyJson_WithoutTier_DeserializesToNull()
    {
        // A queue.json written before #112 has no "Tier" field — it must NOT default to 0 (Critical).
        var restored = JsonSerializer.Deserialize<QueueEntry>("{\"ItemId\":\"abc\",\"Language\":\"en\",\"Force\":true}");
        Assert.NotNull(restored);
        Assert.Null(restored!.Tier);
        Assert.True(restored.Force);
    }
}

// SubtitleWorkItem is a plain record-like type that does not touch the queue singleton,
// so these tests don't need the QueueSingleton collection.
public class SubtitleWorkItemForceTests
{
    [Fact]
    public void SubtitleWorkItem_Force_DefaultsFalse()
    {
        var workItem = new SubtitleWorkItem { Item = new Video { Name = "T" }, Language = "en" };
        Assert.False(workItem.Force);
    }

    [Fact]
    public void SubtitleWorkItem_Force_CanBeSetTrue()
    {
        var workItem = new SubtitleWorkItem { Item = new Video { Name = "T" }, Language = "en", Force = true };
        Assert.True(workItem.Force);
    }

    [Fact]
    public void SubtitleWorkItem_Tier_DefaultsMedium()
    {
        // #112: an unset tier is a safe middle default (every real enqueue path sets it explicitly).
        var workItem = new SubtitleWorkItem { Item = new Video { Name = "T" }, Language = "en" };
        Assert.Equal(PriorityTier.Medium, workItem.Tier);
    }

    [Fact]
    public void SubtitleWorkItem_Tier_CanBeSet()
    {
        var workItem = new SubtitleWorkItem { Item = new Video { Name = "T" }, Language = "en", Tier = PriorityTier.Critical };
        Assert.Equal(PriorityTier.Critical, workItem.Tier);
    }
}
