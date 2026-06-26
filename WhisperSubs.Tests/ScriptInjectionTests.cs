using System;
using WhisperSubs;
using Xunit;

namespace WhisperSubs.Tests;

// Issue #94: the in-page "Generate Subtitles" button + menu come from a client script the plugin
// injects into Jellyfin's index.html. These tests pin the pure pieces of that injection and the
// config-page status guidance, so a regression can't silently break the integration or its diagnosis.
public class ScriptInjectionTests
{
    [Fact]
    public void ComputeInjection_InsertsTagBeforeHead_WhenAbsent()
    {
        var html = "<html><head><title>Jellyfin</title></head><body></body></html>";
        var (outcome, newHtml) = Plugin.ComputeInjection(html);

        Assert.Equal(ScriptInjectionOutcome.Injected, outcome);
        Assert.NotNull(newHtml);
        Assert.Contains(Plugin.ScriptTag, newHtml);
        // Inserted BEFORE </head>, not after.
        Assert.True(
            newHtml!.IndexOf(Plugin.ScriptTag, StringComparison.Ordinal)
            < newHtml.IndexOf("</head>", StringComparison.Ordinal),
            "script tag must be inserted before </head>");
    }

    [Fact]
    public void ComputeInjection_AlreadyPresent_NoChange()
    {
        var html = "<html><head>" + Plugin.ScriptTag + "</head><body></body></html>";
        var (outcome, newHtml) = Plugin.ComputeInjection(html);

        Assert.Equal(ScriptInjectionOutcome.AlreadyPresent, outcome);
        Assert.Null(newHtml);
    }

    [Fact]
    public void ComputeInjection_IsIdempotent_ReinjectingResultIsAlreadyPresent()
    {
        var injected = Plugin.ComputeInjection("<html><head></head></html>").NewHtml!;
        var (outcome, _) = Plugin.ComputeInjection(injected);
        Assert.Equal(ScriptInjectionOutcome.AlreadyPresent, outcome);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<html><body>no head element here</body></html>")]
    public void ComputeInjection_NoHeadTag_WhenEmptyOrHeadless(string? html)
    {
        var (outcome, newHtml) = Plugin.ComputeInjection(html);
        Assert.Equal(ScriptInjectionOutcome.NoHeadTag, outcome);
        Assert.Null(newHtml);
    }

    [Fact]
    public void ComputeInjection_MatchesHeadCaseInsensitively()
    {
        var (outcome, newHtml) = Plugin.ComputeInjection("<HTML><HEAD></HEAD></HTML>");
        Assert.Equal(ScriptInjectionOutcome.Injected, outcome);
        Assert.Contains(Plugin.ScriptTag, newHtml!);
    }

    [Fact]
    public void ComputeInjection_InsertsOnlyOnce_WithMultipleHeadTags()
    {
        // Two </head> must not double-inject; Replace(..., 1) caps at the first occurrence.
        var newHtml = Plugin.ComputeInjection("<head>a</head><head>b</head>").NewHtml!;
        var first = newHtml.IndexOf(Plugin.ScriptTag, StringComparison.Ordinal);
        var second = newHtml.IndexOf(Plugin.ScriptTag, first + 1, StringComparison.Ordinal);
        Assert.True(first >= 0, "tag should be present");
        Assert.True(second < 0, "tag should appear exactly once");
    }

    // ── DescribeInjection: the config-panel guidance for each observable state ──

    [Fact]
    public void DescribeInjection_IndexMissing_IsError()
    {
        var (level, message) = Plugin.DescribeInjection(indexExists: false, scriptTagPresent: false, writable: false);
        Assert.Equal("error", level);
        Assert.Contains("index.html", message);
    }

    [Fact]
    public void DescribeInjection_TagPresent_IsOk()
    {
        var (level, message) = Plugin.DescribeInjection(indexExists: true, scriptTagPresent: true, writable: true);
        Assert.Equal("ok", level);
        Assert.Contains("administrator", message); // reminds that the button is admin-only
    }

    [Fact]
    public void DescribeInjection_TagPresent_IsOkEvenWhenNotWritable()
    {
        // "ok" is keyed on the tag being present, NOT on writability — once injected, a read-only
        // root is fine. A future refactor that gates "ok" on writable must fail this.
        var (level, _) = Plugin.DescribeInjection(indexExists: true, scriptTagPresent: true, writable: false);
        Assert.Equal("ok", level);
    }

    [Fact]
    public void DescribeInjection_PresentButNotWritable_IsError_AndMentionsWritable()
    {
        var (level, message) = Plugin.DescribeInjection(indexExists: true, scriptTagPresent: false, writable: false);
        Assert.Equal("error", level);
        Assert.Contains("writable", message);
    }

    [Fact]
    public void DescribeInjection_WritableButNotInjected_IsWarning()
    {
        var (level, message) = Plugin.DescribeInjection(indexExists: true, scriptTagPresent: false, writable: true);
        Assert.Equal("warning", level);
        Assert.Contains("Re-inject", message);
    }
}
