using WhisperSubs.Controller.Workers;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// v4.0 worker pool backward-compatibility. The composition decision must keep a normal single-server
/// install identical to today, and a legacy remote-offload install remote-only.
/// </summary>
public class WorkerPlanTests
{
    [Fact]
    public void NoConfig_IsLocalOnly()
    {
        // The overwhelming common case: no workers, no remote URL ⇒ the host's own whisper, exactly as today.
        var (source, addLocal) = WorkerPlan.Decide(explicitWorkerCount: 0, hasLegacyRemoteUrl: false, enableLocalWorker: true);
        Assert.Equal(WorkerSource.LocalOnly, source);
        Assert.True(addLocal);
    }

    [Fact]
    public void LegacyRemoteUrl_IsRemoteOnly_NeverAddsLocal()
    {
        // Pre-v4, setting a remote URL sent ALL work remote and never touched the local whisper. Preserve
        // that: even with EnableLocalWorker at its default true, a legacy remote install stays remote-only.
        var (source, addLocal) = WorkerPlan.Decide(explicitWorkerCount: 0, hasLegacyRemoteUrl: true, enableLocalWorker: true);
        Assert.Equal(WorkerSource.LegacyRemote, source);
        Assert.False(addLocal);
    }

    [Fact]
    public void ExplicitList_UsesList_PlusLocalWhenEnabled()
    {
        var withLocal = WorkerPlan.Decide(explicitWorkerCount: 2, hasLegacyRemoteUrl: false, enableLocalWorker: true);
        Assert.Equal(WorkerSource.ExplicitList, withLocal.Source);
        Assert.True(withLocal.AddLocal);

        var withoutLocal = WorkerPlan.Decide(explicitWorkerCount: 2, hasLegacyRemoteUrl: true, enableLocalWorker: false);
        Assert.Equal(WorkerSource.ExplicitList, withoutLocal.Source);   // explicit list wins even over a legacy URL
        Assert.False(withoutLocal.AddLocal);
    }
}
