using System;
using System.Reflection;
using System.Text.Json;
using WhisperSubs;
using WhisperSubs.Web;
using Xunit;

namespace WhisperSubs.Tests;

// Issue #108: serve-time index.html injection via the File Transformation plugin, layered on top
// of the existing direct on-disk injection. Tests pin the pure helpers so a regression in the
// wire contract, the transform logic, or the config-panel guidance is caught immediately.
public class FileTransformationTests
{
    // ── A. NormalizeInjection ──────────────────────────────────────────────────

    [Fact]
    public void NormalizeInjection_NullInput_ReturnsEmpty()
    {
        Assert.Equal("", Plugin.NormalizeInjection(null));
    }

    [Fact]
    public void NormalizeInjection_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal("", Plugin.NormalizeInjection(""));
    }

    [Fact]
    public void NormalizeInjection_NoHeadAndNoTag_ReturnsUnchanged()
    {
        const string html = "<html><body>no head element here</body></html>";
        Assert.Equal(html, Plugin.NormalizeInjection(html));
    }

    [Fact]
    public void NormalizeInjection_NoHeadButTagPresent_ReturnsUnchanged_TagNotStripped()
    {
        // Key safety rule: when there is no </head> anchor to re-insert after, NormalizeInjection
        // must return the original input intact — it must never strip an existing tag and leave
        // the page without one.
        var html = "<body>" + Plugin.ScriptTag + "</body>";
        Assert.Equal(html, Plugin.NormalizeInjection(html));
    }

    [Fact]
    public void NormalizeInjection_SimpleHtml_InsertsTagBeforeHead()
    {
        const string html = "<html><head></head><body></body></html>";
        var result = Plugin.NormalizeInjection(html);
        Assert.Contains(Plugin.ScriptTag + "\n</head>", result);
    }

    [Fact]
    public void NormalizeInjection_IsIdempotent()
    {
        const string html = "<html><head><title>Jellyfin</title></head><body></body></html>";
        var once = Plugin.NormalizeInjection(html);
        var twice = Plugin.NormalizeInjection(once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void NormalizeInjection_CanonicalInput_ReturnsByteIdentical()
    {
        // An html that already contains exactly ScriptTag + "\n</head>" is already canonical —
        // the transform must not mutate it at all (byte-identical, not just semantically equivalent).
        var canonical = "<html><head>" + Plugin.ScriptTag + "\n</head><body></body></html>";
        Assert.Equal(canonical, Plugin.NormalizeInjection(canonical));
    }

    [Fact]
    public void NormalizeInjection_DuplicateTags_CollapseToExactlyOne()
    {
        var html = "<html><head>" + Plugin.ScriptTag + "</head><body>" + Plugin.ScriptTag + "</body></html>";
        var result = Plugin.NormalizeInjection(html);
        var count = result.Split(Plugin.ScriptTag).Length - 1;
        Assert.Equal(1, count);
    }

    [Fact]
    public void NormalizeInjection_VariantTag_NormalizesToCanonical()
    {
        // A <script> with extra attributes referencing whisperSubs.js (e.g. from a hand-edit or
        // a historical injection format) must be replaced with exactly the canonical ScriptTag.
        const string variant = "<script defer src=\"configurationpage?name=whisperSubs.js\"></script>";
        var html = "<html><head>" + variant + "</head><body></body></html>";
        var result = Plugin.NormalizeInjection(html);

        var canonicalCount = result.Split(Plugin.ScriptTag).Length - 1;
        Assert.Equal(1, canonicalCount);
        Assert.DoesNotContain(variant, result);
    }

    [Fact]
    public void NormalizeInjection_CaseInsensitiveHead_Works()
    {
        var result = Plugin.NormalizeInjection("<html><HEAD></HEAD></html>");
        Assert.Contains(Plugin.ScriptTag, result);
    }

    [Fact]
    public void NormalizeInjection_MultipleHeadTags_InsertsOnlyBeforeFirst()
    {
        var html = "<head>a</head><head>b</head>";
        var result = Plugin.NormalizeInjection(html);

        var count = result.Split(Plugin.ScriptTag).Length - 1;
        Assert.Equal(1, count);

        var tagIndex = result.IndexOf(Plugin.ScriptTag, StringComparison.Ordinal);
        var firstHeadEnd = result.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        Assert.True(tagIndex >= 0, "tag must be present in the result");
        Assert.True(tagIndex < firstHeadEnd, "tag must be inserted before the first </head>");
    }

    // ── B. ResolveInjectionMode truth table ───────────────────────────────────

    [Theory]
    [InlineData(true,  true,  "direct+file-transformation")]
    [InlineData(true,  false, "direct")]
    [InlineData(false, true,  "file-transformation")]
    [InlineData(false, false, "none")]
    public void ResolveInjectionMode_ReturnsModeForAllInputCombinations(
        bool scriptTagPresent, bool ftRegistered, string expected)
    {
        Assert.Equal(expected, Plugin.ResolveInjectionMode(scriptTagPresent, ftRegistered));
    }

    // ── C. DescribeInjection — new fileTransformationRegistered behaviours ────

    [Fact]
    public void DescribeInjection_FtRegisteredTagAbsent_IsOkMentionsServeTimeAndReadOnly()
    {
        // (a) FT registered but no tag on disk → serve-time OK, ideal for read-only web roots.
        var (level, message) = Plugin.DescribeInjection(
            indexExists: true, scriptTagPresent: false, writable: false,
            indexHtmlPath: "/web/index.html", fileTransformationRegistered: true);

        Assert.Equal("ok", level);
        Assert.Contains("File Transformation", message);
        Assert.Contains("serve time", message);
        Assert.Contains("read-only", message);
    }

    [Fact]
    public void DescribeInjection_TagPresentAndFtRegistered_IsOkMentionsAlsoRegistered()
    {
        // (b) Both on-disk tag and FT registered → "also registered" keeps the served page canonical.
        var (level, message) = Plugin.DescribeInjection(
            indexExists: true, scriptTagPresent: true, writable: true,
            indexHtmlPath: "/web/index.html", fileTransformationRegistered: true);

        Assert.Equal("ok", level);
        Assert.Contains("also registered with the File Transformation plugin", message);
    }

    [Fact]
    public void DescribeInjection_TagPresentFtNotRegistered_IsOkWithoutFileTransformationMention()
    {
        // (c) Legacy path: tag on disk, FT not installed → ok message must not mention FT at all.
        var (level, message) = Plugin.DescribeInjection(
            indexExists: true, scriptTagPresent: true, writable: true,
            indexHtmlPath: "/web/index.html", fileTransformationRegistered: false);

        Assert.Equal("ok", level);
        Assert.DoesNotContain("File Transformation", message);
    }

    [Fact]
    public void DescribeInjection_NotWritableFtNotRegistered_IsErrorMentionsFtInstallAndLegacyRemediation()
    {
        // (d) Not writable, FT not installed → error that recommends installing FT (with manifest URL)
        // AND still provides the legacy chown/chmod remediation so both paths are actionable.
        const string path = "/usr/share/jellyfin/web/index.html";
        var (level, message) = Plugin.DescribeInjection(
            indexExists: true, scriptTagPresent: false, writable: false,
            indexHtmlPath: path, fileTransformationRegistered: false);

        Assert.Equal("error", level);
        Assert.Contains("File Transformation", message);
        Assert.Contains("iamparadox.dev", message);
        Assert.Contains("chown root:jellyfin", message);
        Assert.Contains("chmod 664", message);
        Assert.Contains("\"" + path + "\"", message);
    }

    [Fact]
    public void DescribeInjection_IndexMissing_IsError_RegardlessOfFtRegistered()
    {
        // (e) Missing index.html always errors, even when FT is registered (FT can't serve a
        // non-existent file either).
        var (level, message) = Plugin.DescribeInjection(
            indexExists: false, scriptTagPresent: false, writable: false,
            indexHtmlPath: "/web/index.html", fileTransformationRegistered: true);

        Assert.Equal("error", level);
        Assert.Contains("index.html", message);
    }

    // ── D. TransformIndexHtml ─────────────────────────────────────────────────

    [Fact]
    public void TransformIndexHtml_NullInput_ReturnsEmpty()
    {
        Assert.Equal("", WebFileTransformation.TransformIndexHtml(null));
    }

    [Fact]
    public void TransformIndexHtml_NullContents_ReturnsEmpty()
    {
        var input = new WebFileTransformation.FileTransformationInput { Contents = null };
        Assert.Equal("", WebFileTransformation.TransformIndexHtml(input));
    }

    [Fact]
    public void TransformIndexHtml_NoHead_ReturnsContentsUnchanged()
    {
        const string html = "<body>no head element here</body>";
        var input = new WebFileTransformation.FileTransformationInput { Contents = html };
        Assert.Equal(html, WebFileTransformation.TransformIndexHtml(input));
    }

    [Fact]
    public void TransformIndexHtml_WithHead_InjectsCanonicalTagExactlyOnce()
    {
        const string html = "<html><head><title>Jellyfin</title></head><body></body></html>";
        var input = new WebFileTransformation.FileTransformationInput { Contents = html };
        var result = WebFileTransformation.TransformIndexHtml(input);
        var count = result.Split(Plugin.ScriptTag).Length - 1;
        Assert.Equal(1, count);
    }

    [Fact]
    public void TransformIndexHtml_ReflectionContract_MethodSignaturePinnedForFileTransformationPlugin()
    {
        // The File Transformation plugin invokes this method via reflection, resolving it by the
        // exact strings stored in callbackClass ("WhisperSubs.Web.WebFileTransformation") and
        // callbackMethod ("TransformIndexHtml"). It expects public static, returns string, and
        // accepts exactly one parameter of type FileTransformationInput. A rename, visibility
        // change, or parameter-type change would silently break serve-time injection for every
        // Jellyfin user with the FT plugin installed — this test catches that at CI time.
        var method = typeof(WebFileTransformation).GetMethod(
            "TransformIndexHtml",
            BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(method);
        Assert.Equal(typeof(string), method!.ReturnType);
        var parameters = method.GetParameters();
        Assert.Single(parameters);
        Assert.Equal(typeof(WebFileTransformation.FileTransformationInput), parameters[0].ParameterType);
    }

    // ── E. BuildRegistrationJson ──────────────────────────────────────────────

    [Fact]
    public void BuildRegistrationJson_ProducesCorrectCamelCaseProperties()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        const string fileNamePattern = "index.html";
        const string callbackAssembly = "WhisperSubs, Version=1.0.0.0";
        const string callbackClass = "WhisperSubs.Web.WebFileTransformation";
        const string callbackMethod = "TransformIndexHtml";

        var json = WebFileTransformation.BuildRegistrationJson(
            id, fileNamePattern, callbackAssembly, callbackClass, callbackMethod);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // All five keys must be present in camelCase with exact values.
        Assert.True(root.TryGetProperty("id", out var idProp), "property 'id' must exist (camelCase)");
        Assert.Equal(id.ToString(), idProp.GetString());

        Assert.True(root.TryGetProperty("fileNamePattern", out var fnp), "property 'fileNamePattern' must exist");
        Assert.Equal(fileNamePattern, fnp.GetString());

        Assert.True(root.TryGetProperty("callbackAssembly", out var ca), "property 'callbackAssembly' must exist");
        Assert.Equal(callbackAssembly, ca.GetString());

        Assert.True(root.TryGetProperty("callbackClass", out var cc), "property 'callbackClass' must exist");
        Assert.Equal(callbackClass, cc.GetString());

        Assert.True(root.TryGetProperty("callbackMethod", out var cm), "property 'callbackMethod' must exist");
        Assert.Equal(callbackMethod, cm.GetString());

        // PascalCase keys must NOT exist — confirms true camelCase serialization, not accidental match.
        Assert.False(root.TryGetProperty("Id", out _), "PascalCase 'Id' must not exist");
        Assert.False(root.TryGetProperty("FileNamePattern", out _), "PascalCase 'FileNamePattern' must not exist");
    }

    // ── F. FileTransformationState and ScriptInjectionStatus defaults ─────────

    [Fact]
    public void FileTransformationState_NotChecked_HasAllDefaults()
    {
        var state = FileTransformationState.NotChecked;
        Assert.False(state.Present);
        Assert.False(state.Registered);
        Assert.Equal("", state.Version);
        Assert.Equal("", state.Error);
    }

    [Fact]
    public void ScriptInjectionStatus_NewIssue108Fields_HaveCorrectDefaults()
    {
        var status = new ScriptInjectionStatus();
        Assert.Equal("", status.Mode);
        Assert.Equal("unknown", status.ServedHtmlVerified);
        Assert.False(status.FileTransformationPresent);
        Assert.False(status.FileTransformationRegistered);
        Assert.Equal("", status.FileTransformationVersion);
        Assert.Equal("", status.FileTransformationError);
    }
}
