using System.Collections.Generic;
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

    [Theory]
    [InlineData("http://Host:9010", "http://host:9010/")]
    [InlineData("http://host:9010", "HTTP://host:9010")]
    [InlineData("http://host:9010/", "http://host:9010")]
    public void NormalizeEndpoint_EquivalentUrls_MatchEachOther(string a, string b)
        => Assert.Equal(WorkerConfigValidation.NormalizeEndpoint(a), WorkerConfigValidation.NormalizeEndpoint(b));

    [Theory]
    [InlineData("http://host:9010", "http://host:9011")]
    [InlineData("http://hostA:9010", "http://hostB:9010")]
    [InlineData("http://host:9010", "https://host:9010")]
    public void NormalizeEndpoint_DifferentUrls_DoNotMatch(string a, string b)
        => Assert.NotEqual(WorkerConfigValidation.NormalizeEndpoint(a), WorkerConfigValidation.NormalizeEndpoint(b));

    [Fact]
    public void CheckDuplicateEndpoints_TwoEnabledSameEndpoint_OneWarning()
    {
        var workers = new List<WhisperWorker>
        {
            W(url: "http://host:9010"),
            W(url: "http://Host:9010/"),
        };

        var warnings = WorkerConfigValidation.CheckDuplicateEndpoints(workers);

        Assert.Single(warnings);
        Assert.Contains("host:9010", warnings[0]);
    }

    [Fact]
    public void CheckDuplicateEndpoints_OneDisabled_NoWarning()
    {
        var enabled = W(url: "http://host:9010");
        var disabled = W(url: "http://Host:9010/");
        disabled.Enabled = false;
        var workers = new List<WhisperWorker> { enabled, disabled };

        Assert.Empty(WorkerConfigValidation.CheckDuplicateEndpoints(workers));
    }

    [Fact]
    public void CheckDuplicateEndpoints_DistinctEndpoints_NoWarning()
    {
        var workers = new List<WhisperWorker>
        {
            W(url: "http://host-a:9010"),
            W(url: "http://host-b:9010"),
        };

        Assert.Empty(WorkerConfigValidation.CheckDuplicateEndpoints(workers));
    }
}
