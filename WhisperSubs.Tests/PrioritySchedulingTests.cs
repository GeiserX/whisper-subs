using System;
using System.Linq;
using WhisperSubs.Controller;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Issue #112: pure tier-resolution, legacy-migration and quota-window helpers. These are the
/// security-critical decisions (server-assigned tier, quota enforcement) kept in unit-tested helpers.
/// </summary>
public class PrioritySchedulingTests
{
    // ── ClampTier ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-5, PriorityTier.Critical)]
    [InlineData(0, PriorityTier.Critical)]
    [InlineData(2, PriorityTier.Medium)]
    [InlineData(4, PriorityTier.Background)]
    [InlineData(99, PriorityTier.Background)]
    public void ClampTier_KeepsWithinValidRange(int input, PriorityTier expected)
        => Assert.Equal(expected, PriorityScheduling.ClampTier(input));

    // ── ResolveTier (server-assigned from role) ──────────────────────────────

    [Fact]
    public void ResolveTier_MapsEachRequesterToItsConfiguredTier()
    {
        Assert.Equal(PriorityTier.High,
            PriorityScheduling.ResolveTier(RequesterKind.Admin, PriorityTier.High, PriorityTier.Medium, PriorityTier.Background));
        Assert.Equal(PriorityTier.Medium,
            PriorityScheduling.ResolveTier(RequesterKind.User, PriorityTier.High, PriorityTier.Medium, PriorityTier.Background));
        Assert.Equal(PriorityTier.Background,
            PriorityScheduling.ResolveTier(RequesterKind.BackgroundSweep, PriorityTier.High, PriorityTier.Medium, PriorityTier.Background));
    }

    [Fact]
    public void ResolveTier_HonoursCustomMapping()
    {
        // The maintainer's server: admin → Critical, users → Medium.
        Assert.Equal(PriorityTier.Critical,
            PriorityScheduling.ResolveTier(RequesterKind.Admin, PriorityTier.Critical, PriorityTier.Medium, PriorityTier.Background));
    }

    [Fact]
    public void ResolveTier_UnknownRequester_FallsBackToMedium()
    {
        // Defensive default arm — an out-of-range requester kind maps to a safe middle tier.
        Assert.Equal(PriorityTier.Medium,
            PriorityScheduling.ResolveTier((RequesterKind)99, PriorityTier.High, PriorityTier.Low, PriorityTier.Background));
    }

    // ── NormalizeRestoredTier (legacy migration) ─────────────────────────────

    [Fact]
    public void NormalizeRestoredTier_NullLegacyEntry_MapsToHigh_NotCritical()
    {
        // The migration gotcha: a legacy queue.json entry has no tier. It must map to High, NOT
        // Critical(0), or old entries would silently outrank every new admin request.
        Assert.Equal(PriorityTier.High, PriorityScheduling.NormalizeRestoredTier(null));
    }

    [Theory]
    [InlineData(0, PriorityTier.Critical)]
    [InlineData(2, PriorityTier.Medium)]
    [InlineData(4, PriorityTier.Background)]
    [InlineData(-1, PriorityTier.Critical)]  // clamped
    [InlineData(50, PriorityTier.Background)] // clamped
    public void NormalizeRestoredTier_PresentValue_IsClamped(int persisted, PriorityTier expected)
        => Assert.Equal(expected, PriorityScheduling.NormalizeRestoredTier(persisted));

    // ── Stronger ─────────────────────────────────────────────────────────────

    [Fact]
    public void Stronger_ReturnsHigherPriority()
    {
        Assert.Equal(PriorityTier.Critical, PriorityScheduling.Stronger(PriorityTier.Critical, PriorityTier.Medium));
        Assert.Equal(PriorityTier.Critical, PriorityScheduling.Stronger(PriorityTier.Medium, PriorityTier.Critical));
        Assert.Equal(PriorityTier.High, PriorityScheduling.Stronger(PriorityTier.High, PriorityTier.High));
    }

    // ── EvaluateQuota (sliding window) ───────────────────────────────────────

    private static readonly long Hour = TimeSpan.FromHours(1).Ticks;

    [Fact]
    public void EvaluateQuota_UnderLimit_IsAllowed()
    {
        long now = 1_000_000 * Hour;
        var prior = new[] { now - Hour, now - 2 * Hour };
        var d = PriorityScheduling.EvaluateQuota(prior, now, 24 * Hour, 5);

        Assert.True(d.Allowed);
        Assert.Equal(2, d.UsedInWindow);
        Assert.Equal(2, d.KeptTicks.Count);
    }

    [Fact]
    public void EvaluateQuota_AtLimit_IsDenied()
    {
        long now = 1_000_000 * Hour;
        var prior = new[] { now - Hour, now - 2 * Hour, now - 3 * Hour };
        var d = PriorityScheduling.EvaluateQuota(prior, now, 24 * Hour, 3);

        Assert.False(d.Allowed);
        Assert.Equal(3, d.UsedInWindow);
    }

    [Fact]
    public void EvaluateQuota_PrunesTimestampsOutsideWindow()
    {
        long now = 1_000_000 * Hour;
        // Two recent, two older than the 24h window.
        var prior = new[] { now - Hour, now - 2 * Hour, now - 30 * Hour, now - 48 * Hour };
        var d = PriorityScheduling.EvaluateQuota(prior, now, 24 * Hour, 5);

        Assert.Equal(2, d.UsedInWindow);       // only the two inside the window count
        Assert.Equal(2, d.KeptTicks.Count);    // the pruned list is what gets persisted
        Assert.True(d.Allowed);
    }

    [Fact]
    public void EvaluateQuota_KeptTicks_AreSortedAscending()
    {
        long now = 1_000_000 * Hour;
        var prior = new[] { now - 2 * Hour, now - Hour, now - 3 * Hour };
        var d = PriorityScheduling.EvaluateQuota(prior, now, 24 * Hour, 5);

        Assert.Equal(d.KeptTicks.OrderBy(t => t).ToArray(), d.KeptTicks.ToArray());
    }

    [Fact]
    public void EvaluateQuota_ZeroOrNegativeLimit_IsUnlimited()
    {
        long now = 1_000_000 * Hour;
        var prior = Enumerable.Range(1, 100).Select(i => now - i * Hour).ToArray();
        Assert.True(PriorityScheduling.EvaluateQuota(prior, now, 24 * Hour, 0).Allowed);
    }

    [Fact]
    public void EvaluateQuota_NonPositiveWindow_KeepsAllTimestamps()
    {
        long now = 1_000_000 * Hour;
        var prior = new[] { now - Hour, now - 1000 * Hour };
        var d = PriorityScheduling.EvaluateQuota(prior, now, 0, 5);

        Assert.Equal(2, d.UsedInWindow); // no time-based pruning
    }
}
