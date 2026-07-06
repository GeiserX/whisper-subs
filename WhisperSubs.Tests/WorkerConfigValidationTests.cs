using WhisperSubs.Configuration;
using WhisperSubs.Controller.Workers;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// v4.0 config UI: pure validation of a worker row, so a malformed endpoint/concurrency/cost is rejected
/// before it reaches the pool.
/// </summary>
public class WorkerConfigValidationTests
{
    private static WhisperWorker W(string url = "https://gpu.lan:8000", int max = 1, double cost = 0)
        => new WhisperWorker { ApiUrl = url, MaxConcurrency = max, CostWeight = cost };

    [Fact]
    public void ValidHttpsWorker_Ok()
    {
        var (ok, err) = WorkerConfigValidation.Validate(W());
        Assert.True(ok);
        Assert.Null(err);
    }

    [Fact]
    public void HttpUrl_Ok()
        => Assert.True(WorkerConfigValidation.Validate(W(url: "http://192.168.10.110:8000")).Ok);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankUrl_Fails(string url)
    {
        var (ok, err) = WorkerConfigValidation.Validate(W(url: url));
        Assert.False(ok);
        Assert.NotNull(err);
    }

    [Theory]
    [InlineData("not-a-url")]
    [InlineData("ftp://host/x")]
    [InlineData("/relative/path")]
    public void NonHttpUrl_Fails(string url)
        => Assert.False(WorkerConfigValidation.Validate(W(url: url)).Ok);

    [Fact]
    public void MaxConcurrencyBelowOne_Fails()
        => Assert.False(WorkerConfigValidation.Validate(W(max: 0)).Ok);

    [Fact]
    public void NegativeCostWeight_Fails()
        => Assert.False(WorkerConfigValidation.Validate(W(cost: -1)).Ok);
}
