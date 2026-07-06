using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WhisperSubs.ScheduledTasks;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// v4.1 parallel sweep: pins the in-flight pruning predicate. Only tasks that ran to completion may
/// be dropped from the tracked list — cancelled and faulted tasks must survive pruning so the sweep's
/// final Task.WhenAll still observes their exceptions (pruning on IsCompleted instead would silently
/// orphan a cancelled item's OperationCanceledException).
/// </summary>
public class InFlightTasksTests
{
    [Fact]
    public void PruneCompleted_DropsOnlySuccessfullyCompletedTasks()
    {
        var succeeded = Task.CompletedTask;
        var cancelled = Task.FromCanceled(new CancellationToken(canceled: true));
        var faulted = Task.FromException(new InvalidOperationException("boom"));
        var running = new TaskCompletionSource().Task;

        var inFlight = new List<Task> { succeeded, cancelled, faulted, running };
        var removed = InFlightTasks.PruneCompleted(inFlight);

        Assert.Equal(1, removed);
        Assert.Equal(new List<Task> { cancelled, faulted, running }, inFlight);

        _ = faulted.Exception; // observe so the faulted fixture task never trips the finalizer
    }

    [Fact]
    public void PruneCompleted_EmptyList_IsANoOp()
    {
        var inFlight = new List<Task>();

        Assert.Equal(0, InFlightTasks.PruneCompleted(inFlight));
        Assert.Empty(inFlight);
    }
}
