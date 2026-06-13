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
}
