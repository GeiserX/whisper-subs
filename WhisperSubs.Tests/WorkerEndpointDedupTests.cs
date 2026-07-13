using System.Linq;
using WhisperSubs.Configuration;
using WhisperSubs.Controller.Workers;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Locks the v4.3.1 per-endpoint collapse: two enabled worker rows pointing at ONE physical
/// whisper-server must become a single pool worker (single-request backend can't be oversubscribed),
/// while distinct endpoints stay separate and a normal single-worker config is untouched.
/// </summary>
public class WorkerEndpointDedupTests
{
    private static WhisperWorker Row(string id, string url, int conc = 1)
        => new WhisperWorker { Id = id, Name = id, Enabled = true, ApiUrl = url, MaxConcurrency = conc };

    // ---- NormalizeEndpoint ---------------------------------------------------------------------

    [Theory]
    [InlineData("http://host:9010", "http://host:9010/")]        // trailing slash
    [InlineData("http://host:9010", "HTTP://Host:9010")]         // scheme + host case
    [InlineData("http://host:9010", "  http://host:9010  ")]     // surrounding whitespace
    [InlineData("http://host:80", "http://host")]                // explicit vs default http port
    [InlineData("https://host:443", "https://host/")]            // explicit vs default https port
    public void NormalizeEndpoint_TreatsEquivalentUrlsAsEqual(string a, string b)
    {
        Assert.Equal(WorkerEndpointDedup.NormalizeEndpoint(a), WorkerEndpointDedup.NormalizeEndpoint(b));
    }

    [Theory]
    [InlineData("http://host:9010", "http://host:9011")]         // different port
    [InlineData("http://host-a:9010", "http://host-b:9010")]     // different host
    [InlineData("http://host:9010/a", "http://host:9010/b")]     // different path
    [InlineData("http://host:9010", "https://host:9010")]        // different scheme
    public void NormalizeEndpoint_TreatsDistinctUrlsAsDifferent(string a, string b)
    {
        Assert.NotEqual(WorkerEndpointDedup.NormalizeEndpoint(a), WorkerEndpointDedup.NormalizeEndpoint(b));
    }

    // ---- CollapseByEndpoint --------------------------------------------------------------------

    [Fact]
    public void Collapse_TwoRowsSameEndpoint_BecomeOneWorker()
    {
        var rows = new[] { Row("a", "http://box:9010"), Row("b", "http://box:9010/") };
        var collapsed = WorkerEndpointDedup.CollapseByEndpoint(rows);
        Assert.Single(collapsed);
        Assert.Equal("a", collapsed[0].Id); // first-in-order keeps its identity
    }

    [Fact]
    public void Collapse_SameEndpoint_TakesMinConcurrency()
    {
        var rows = new[] { Row("a", "http://box:9010", conc: 3), Row("b", "http://box:9010", conc: 1) };
        var collapsed = WorkerEndpointDedup.CollapseByEndpoint(rows);
        Assert.Single(collapsed);
        Assert.Equal(1, collapsed[0].MaxConcurrency); // a duplicate can never widen real capacity
    }

    [Fact]
    public void Collapse_DistinctEndpoints_AreKeptSeparate()
    {
        var rows = new[] { Row("a", "http://box-a:9010"), Row("b", "http://box-b:9010") };
        var collapsed = WorkerEndpointDedup.CollapseByEndpoint(rows);
        Assert.Equal(2, collapsed.Count);
        Assert.Equal(new[] { "a", "b" }, collapsed.Select(w => w.Id).ToArray());
    }

    [Fact]
    public void Collapse_SingleWorker_IsUnchanged()
    {
        var rows = new[] { Row("only", "http://box:9010", conc: 2) };
        var collapsed = WorkerEndpointDedup.CollapseByEndpoint(rows);
        Assert.Single(collapsed);
        Assert.Equal("only", collapsed[0].Id);
        Assert.Equal(2, collapsed[0].MaxConcurrency); // no duplicate → no min clamp beyond >=1
    }

    [Fact]
    public void Collapse_DoesNotMutateInputRows()
    {
        var a = Row("a", "http://box:9010", conc: 3);
        var b = Row("b", "http://box:9010", conc: 1);
        WorkerEndpointDedup.CollapseByEndpoint(new[] { a, b });
        Assert.Equal(3, a.MaxConcurrency); // originals untouched
        Assert.Equal(1, b.MaxConcurrency);
    }
}
