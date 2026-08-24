using System;
using WhisperSubs.ScheduledTasks;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Locks the wall-clock budget for one sweep of the subtitle generation task. Per-call deadlines
/// already bound a single transcription; this bounds the sweep itself, which is what let a run
/// continue for eleven hours into the working day. The contract: 0 or less means unlimited (the
/// behaviour before the setting existed), a positive value produces a deadline that many hours after
/// the start, and the boundary instant counts as expired so the sweep never overshoots.
/// </summary>
public class SweepBudgetTests
{
    private static readonly DateTime Start = new(2026, 8, 24, 2, 0, 0, DateTimeKind.Utc);

    // ---- disabled: unlimited, exactly as before the setting existed ----------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void NonPositiveHours_MeansUnlimited(int hours)
    {
        var deadline = SweepBudget.Deadline(hours, Start);

        Assert.Null(deadline);
        Assert.False(SweepBudget.Expired(deadline, Start.AddYears(5)));
    }

    // ---- enabled: deadline is start + N hours ---------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    [InlineData(24)]
    public void PositiveHours_DeadlineIsStartPlusHours(int hours)
    {
        Assert.Equal(Start.AddHours(hours), SweepBudget.Deadline(hours, Start));
    }

    [Fact]
    public void NotExpired_BeforeTheDeadline()
    {
        var deadline = SweepBudget.Deadline(6, Start);

        Assert.False(SweepBudget.Expired(deadline, Start));
        Assert.False(SweepBudget.Expired(deadline, Start.AddHours(5).AddMinutes(59)));
    }

    [Fact]
    public void Expired_AtTheDeadlineInstantAndAfter()
    {
        var deadline = SweepBudget.Deadline(6, Start);

        // The boundary itself counts, so a sweep stops at the budget rather than one item past it.
        Assert.True(SweepBudget.Expired(deadline, Start.AddHours(6)));
        Assert.True(SweepBudget.Expired(deadline, Start.AddHours(11)));
    }

    // ---- a misconfigured huge value must not throw out of AddHours ------------------------------

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(1_000_000_000)]
    public void AbsurdlyLargeHours_SaturateInsteadOfThrowing(int hours)
    {
        // AddHours would throw ArgumentOutOfRangeException and kill the task. Saturating gives an
        // unreachable deadline, which is the "effectively unlimited" the operator asked for.
        var deadline = SweepBudget.Deadline(hours, Start);

        Assert.Equal(DateTime.MaxValue, deadline);
        Assert.False(SweepBudget.Expired(deadline, new DateTime(9999, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
    }

    // ---- the real incident: 02:00 start, still running at 13:00 ---------------------------------

    [Fact]
    public void SixHourDefault_WouldHaveStoppedTheElevenHourRun()
    {
        var deadline = SweepBudget.Deadline(6, Start);

        var whenItWasFoundStillRunning = new DateTime(2026, 8, 24, 13, 0, 0, DateTimeKind.Utc);
        Assert.True(SweepBudget.Expired(deadline, whenItWasFoundStillRunning));
    }
}
