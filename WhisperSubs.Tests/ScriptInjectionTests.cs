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
        var (level, message) = Plugin.DescribeInjection(indexExists: false, scriptTagPresent: false, writable: false, indexHtmlPath: "");
        Assert.Equal("error", level);
        Assert.Contains("index.html", message);
    }

    [Fact]
    public void DescribeInjection_TagPresent_IsOk()
    {
        var (level, message) = Plugin.DescribeInjection(indexExists: true, scriptTagPresent: true, writable: true, indexHtmlPath: "/web/index.html");
        Assert.Equal("ok", level);
        Assert.Contains("administrator", message); // reminds that the button is admin-only
    }

    [Fact]
    public void DescribeInjection_TagPresent_IsOkEvenWhenNotWritable()
    {
        // "ok" is keyed on the tag being present, NOT on writability — once injected, a read-only
        // root is fine. A future refactor that gates "ok" on writable must fail this.
        var (level, _) = Plugin.DescribeInjection(indexExists: true, scriptTagPresent: true, writable: false, indexHtmlPath: "/web/index.html");
        Assert.Equal("ok", level);
    }

    [Fact]
    public void DescribeInjection_PresentButNotWritable_IsError_AndMentionsWritable()
    {
        var (level, message) = Plugin.DescribeInjection(
            indexExists: true, scriptTagPresent: false, writable: false,
            indexHtmlPath: "/usr/share/jellyfin/web/index.html");
        Assert.Equal("error", level);
        Assert.Contains("writable", message);
        // The remediation must be copy-paste: concrete chown/chmod, the load-bearing 664 mode (644
        // would NOT grant the jellyfin group write), and the real path QUOTED so paths with spaces
        // stay valid.
        Assert.Contains("chown", message);
        Assert.Contains("chmod", message);
        Assert.Contains("664", message);
        Assert.Contains("\"/usr/share/jellyfin/web/index.html\"", message);
    }

    [Fact]
    public void DescribeInjection_NotWritable_EmptyPath_UsesGenericFallback()
    {
        // Defensive fallback when the path is somehow empty — still actionable, no dangling quotes.
        var (level, message) = Plugin.DescribeInjection(indexExists: true, scriptTagPresent: false, writable: false, indexHtmlPath: "");
        Assert.Equal("error", level);
        Assert.Contains("your index.html", message);
        Assert.Contains("chown", message);
    }

    [Fact]
    public void DescribeInjection_NotWritable_QuotesPathWithSpaces()
    {
        // The whole reason the path is quoted: a web root with spaces must stay a single copy-paste
        // argument. Drop the quotes and chown/chmod would split it — this case catches that regression.
        const string spaced = "/srv/My Media/jellyfin web/index.html";
        var (level, message) = Plugin.DescribeInjection(indexExists: true, scriptTagPresent: false, writable: false, indexHtmlPath: spaced);
        Assert.Equal("error", level);
        Assert.Contains("\"" + spaced + "\"", message); // appears as one quoted token, not bare
    }

    [Fact]
    public void DescribeInjection_WritableButNotInjected_IsWarning()
    {
        var (level, message) = Plugin.DescribeInjection(indexExists: true, scriptTagPresent: false, writable: true, indexHtmlPath: "/web/index.html");
        Assert.Equal("warning", level);
        Assert.Contains("Re-inject", message);
    }

    // ── Windows remediation (issue #149) ──────────────────────────────────────
    // On a bare-metal Windows install the web root normally sits under C:\Program Files, which the
    // Jellyfin service account cannot write — so "not writable" is the COMMON case there. Handing that
    // admin a `sudo chown root:jellyfin` line gives them nothing to run.

    [Theory]
    [InlineData(@"C:\Program Files\Jellyfin\Server\jellyfin-web\index.html")]
    [InlineData(@"D:\Jellyfin\jellyfin-web\index.html")]
    [InlineData(@"\\nas\jellyfin\web\index.html")]
    public void DescribeInjection_NotWritable_WindowsPath_GivesWindowsRemediation(string path)
    {
        var (level, message) = Plugin.DescribeInjection(
            indexExists: true, scriptTagPresent: false, writable: false, indexHtmlPath: path);

        Assert.Equal("error", level);
        // Actionable on Windows: the real tool, the real path (quoted — these paths contain spaces).
        Assert.Contains("icacls", message);
        Assert.Contains("\"" + path + "\"", message);
        // And NOT the POSIX advice, which is the whole defect being fixed.
        Assert.DoesNotContain("sudo", message);
        Assert.DoesNotContain("chown", message);
        Assert.DoesNotContain("chmod", message);
        // The File Transformation route is OS-independent and must survive on both branches.
        Assert.Contains("File Transformation", message);
    }

    [Fact]
    public void DescribeInjection_NotWritable_PosixPath_KeepsPosixRemediation()
    {
        // Guard the other side of the branch: adding Windows guidance must not weaken the Linux path.
        var (level, message) = Plugin.DescribeInjection(
            indexExists: true, scriptTagPresent: false, writable: false,
            indexHtmlPath: "/usr/share/jellyfin/web/index.html");

        Assert.Equal("error", level);
        Assert.Contains("chown", message);
        Assert.Contains("664", message);
        Assert.DoesNotContain("icacls", message);
    }

    [Theory]
    [InlineData(@"C:\web\index.html", true)]
    [InlineData(@"c:/web/index.html", true)]
    [InlineData(@"\\host\share\index.html", true)]
    [InlineData("/usr/share/jellyfin/web/index.html", false)]
    [InlineData("/srv/My Media/index.html", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("C:", false)]           // too short to be a rooted path
    [InlineData("relative/index.html", false)]
    [InlineData("http://host/index.html", false)]  // a scheme is not a drive letter
    public void IsWindowsStylePath_ClassifiesFromTheStringAlone(string? path, bool expected)
    {
        Assert.Equal(expected, Plugin.IsWindowsStylePath(path));
    }
}
