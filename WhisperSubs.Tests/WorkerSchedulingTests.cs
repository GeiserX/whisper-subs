using System;
using System.Collections.Generic;
using WhisperSubs.Controller.Workers;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// v4.0 worker pool: the pure selection policy. Verifies the hard capability filter and the cost-weighted
/// "prefer local, burst to paid only when locals are saturated" behaviour that must hold for any topology.
/// </summary>
public class WorkerSchedulingTests
{
    private static WorkerCapabilities Caps(bool local = true, double cost = 0, int maxConc = 1,
        bool canTranslate = true, int priority = 0, string[]? models = null)
        => new()
        {
            IsLocal = local, CostWeight = cost, MaxConcurrency = maxConc, CanTranslate = canTranslate,
            Priority = priority, Models = new HashSet<string>(models ?? Array.Empty<string>())
        };

    private static WorkerSlot Slot(string id, bool healthy = true, int inFlight = 0, WorkerCapabilities? caps = null)
        => new(id, healthy, inFlight, caps ?? Caps());

    private static readonly JobRequirements AnyJob = new(false, null);

    // ── CanServe (hard constraints) ──
    [Fact] public void CanServe_HealthyFreeWorker_True()
        => Assert.True(WorkerScheduling.CanServe(Slot("a"), AnyJob));

    [Fact] public void CanServe_Unhealthy_False()
        => Assert.False(WorkerScheduling.CanServe(Slot("a", healthy: false), AnyJob));

    [Fact] public void CanServe_AtCapacity_False()
        => Assert.False(WorkerScheduling.CanServe(Slot("a", inFlight: 1, caps: Caps(maxConc: 1)), AnyJob));

    [Fact] public void CanServe_TranslateJob_NeedsTranslateCapability()
    {
        var job = new JobRequirements(true, null);
        Assert.False(WorkerScheduling.CanServe(Slot("a", caps: Caps(canTranslate: false)), job));
        Assert.True(WorkerScheduling.CanServe(Slot("b", caps: Caps(canTranslate: true)), job));
    }

    [Fact] public void CanServe_ModelConstraint()
    {
        var job = new JobRequirements(false, "large-v3");
        Assert.True(WorkerScheduling.CanServe(Slot("any"), job));                                        // empty models = any
        Assert.True(WorkerScheduling.CanServe(Slot("has", caps: Caps(models: new[] { "large-v3" })), job));
        Assert.False(WorkerScheduling.CanServe(Slot("no", caps: Caps(models: new[] { "small" })), job));
    }

    // ── Pick (prefer local, burst to paid) ──
    [Fact] public void Pick_PrefersLocalOverPaid()
    {
        var local = Slot("local", caps: Caps(local: true, cost: 0));
        var cloud = Slot("cloud", caps: Caps(local: false, cost: 5));
        Assert.Equal("local", WorkerScheduling.Pick(new[] { cloud, local }, AnyJob)!.Value.Id);
    }

    [Fact] public void Pick_BurstsToPaid_OnlyWhenLocalsSaturated()
    {
        var localFull = Slot("local", inFlight: 1, caps: Caps(local: true, cost: 0, maxConc: 1));
        var cloud = Slot("cloud", caps: Caps(local: false, cost: 5));
        Assert.Equal("cloud", WorkerScheduling.Pick(new[] { localFull, cloud }, AnyJob)!.Value.Id);
    }

    [Fact] public void Pick_PrefersLeastBusyAmongEqualCost()
    {
        var busy = Slot("busy", inFlight: 1, caps: Caps(cost: 0, maxConc: 3));
        var idle = Slot("idle", inFlight: 0, caps: Caps(cost: 0, maxConc: 3));
        Assert.Equal("idle", WorkerScheduling.Pick(new[] { busy, idle }, AnyJob)!.Value.Id);
    }

    [Fact] public void Pick_PriorityTiebreak()
    {
        var lo = Slot("x", caps: Caps(cost: 0, priority: 10));
        var hi = Slot("y", caps: Caps(cost: 0, priority: 1));
        Assert.Equal("y", WorkerScheduling.Pick(new[] { lo, hi }, AnyJob)!.Value.Id);
    }

    [Fact] public void Pick_DeterministicTiebreakById()
    {
        // Identical everything → lowest ordinal Id wins, deterministically.
        Assert.Equal("a", WorkerScheduling.Pick(new[] { Slot("b"), Slot("a") }, AnyJob)!.Value.Id);
    }

    [Fact] public void Pick_NullWhenNothingFeasible()
    {
        var full = Slot("a", inFlight: 1, caps: Caps(maxConc: 1));
        var down = Slot("b", healthy: false);
        Assert.Null(WorkerScheduling.Pick(new[] { full, down }, AnyJob));
        Assert.Null(WorkerScheduling.Pick(Array.Empty<WorkerSlot>(), AnyJob));
    }
}
