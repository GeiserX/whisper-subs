using System.Net;
using WhisperSubs.Configuration;
using WhisperSubs.Controller.Workers;
using WhisperSubs.Providers;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Branch coverage for the defensive edges of the v4.5 upload/diagnostics code (issue #138). These are the
/// paths that only run when something is already wrong — a blank URL, a caller passing the wrong codec, a
/// zero-length file — which is exactly when they must not throw or misreport.
/// </summary>
public class RemoteUploadEdgeCaseTests
{
    // ---- UpstreamErrorSanitizer edges ----------------------------------------------------------

    [Fact]
    public void AbsurdlySmallMaxLengthIsClampedNotHonoured()
    {
        // A caller asking for 3 characters must still get a usable, safely-terminated string.
        var sanitized = UpstreamErrorSanitizer.Sanitize(new string('x', 200), null, maxLength: 3);
        Assert.True(sanitized.Length is > 3 and <= 16);
    }

    [Fact]
    public void ShortInputIsReturnedWithoutTruncationMarker()
    {
        var sanitized = UpstreamErrorSanitizer.Sanitize("model not found", null);
        Assert.Equal("model not found", sanitized);
        Assert.DoesNotContain("…", sanitized, System.StringComparison.Ordinal);
    }

    [Fact]
    public void LongProseIsTruncatedWithAMarker()
    {
        // Deliberately ORDINARY prose: a long run of a single character would be swallowed by the
        // high-entropy redaction rule and never reach the truncation branch at all (which is exactly the
        // trap an earlier version of this test fell into).
        var body = string.Join(" ", System.Linq.Enumerable.Repeat("the endpoint refused this request", 40));

        var sanitized = UpstreamErrorSanitizer.Sanitize(body, null, maxLength: 120);

        Assert.True(sanitized.Length <= 120);
        Assert.EndsWith("…", sanitized, System.StringComparison.Ordinal);
        Assert.StartsWith("the endpoint refused", sanitized, System.StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SanitizeEndpointHandlesBlank(string? url)
    {
        Assert.Equal(string.Empty, UpstreamErrorSanitizer.SanitizeEndpoint(url));
    }

    [Fact]
    public void SanitizeEndpointFallsBackForNonAbsoluteInput()
    {
        // Not a parseable absolute URL (a typo an admin can realistically save) — must not throw, and must
        // still scrub anything secret-shaped.
        var sanitized = UpstreamErrorSanitizer.SanitizeEndpoint("not a url api_key=supersecretvalue");
        Assert.DoesNotContain("supersecretvalue", sanitized, System.StringComparison.Ordinal);
    }

    // ---- RemoteUploadFormat edges ---------------------------------------------------------------

    [Theory]
    [InlineData("wav", ".wav")]
    [InlineData("flac", ".flac")]
    [InlineData("opus", ".ogg")]
    [InlineData("bogus", ".wav")]
    public void ExtensionMatchesTheCodec(string codec, string expected)
    {
        Assert.Equal(expected, RemoteUploadFormat.Extension(codec));
    }

    [Theory]
    [InlineData("", "/tmp/out.flac")]
    [InlineData("   ", "/tmp/out.flac")]
    public void BuildArgumentsRejectsBlankSource(string source, string target)
    {
        Assert.Throws<System.ArgumentException>(
            () => RemoteUploadFormat.BuildFfmpegArguments(source, target, "flac"));
    }

    [Theory]
    [InlineData("/tmp/in.wav", "")]
    [InlineData("/tmp/in.wav", "   ")]
    public void BuildArgumentsRejectsBlankTarget(string source, string target)
    {
        Assert.Throws<System.ArgumentException>(
            () => RemoteUploadFormat.BuildFfmpegArguments(source, target, "flac"));
    }

    // ---- RemoteErrorGuidance edges ---------------------------------------------------------------

    [Theory]
    [InlineData(403, "billing")]
    [InlineData(405, "audio uploads")]
    [InlineData(501, "audio uploads")]
    public void MoreStatusesCarryGuidance(int status, string expected)
    {
        Assert.Contains(expected, RemoteErrorGuidance.For((HttpStatusCode)status),
            System.StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0, 25_000_000)]
    [InlineData(76_800_000, 0)]
    [InlineData(-5, -5)]
    public void OversizedDescriptionIsEmptyWithoutUsableNumbers(long sourceBytes, long maxBytes)
    {
        // Never emit a nonsense sentence like "this 0-minute title is 0 KB".
        Assert.Equal(string.Empty, RemoteErrorGuidance.DescribeOversizedUpload(sourceBytes, maxBytes));
    }

    [Fact]
    public void SmallSizesRenderAsKilobytes()
    {
        Assert.Equal("1 KB", RemoteErrorGuidance.FormatBytes(1000));
        Assert.Equal("0 KB", RemoteErrorGuidance.FormatBytes(0));
        Assert.Equal("0 KB", RemoteErrorGuidance.FormatBytes(-10));   // clamped, never negative
    }

    // ---- Negotiation decision (pure) --------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, true)]
    [InlineData(HttpStatusCode.NotAcceptable, true)]
    [InlineData(HttpStatusCode.UnsupportedMediaType, true)]
    [InlineData(HttpStatusCode.UnprocessableEntity, true)]
    [InlineData(HttpStatusCode.Unauthorized, false)]      // a bad key is not a format problem
    [InlineData(HttpStatusCode.RequestEntityTooLarge, false)] // nor is an oversized upload
    [InlineData(HttpStatusCode.InternalServerError, false)]
    public void UnnegotiatedWorkerRetriesOnlyOnFormatCandidateStatuses(HttpStatusCode status, bool expected)
    {
        var exception = new System.Net.Http.HttpRequestException("boom", null, status);
        Assert.Equal(expected,
            RemoteWhisperProvider.ShouldNegotiateAlternateFormat(exception, formatNotYetNegotiated: true));
    }

    [Fact]
    public void OnceNegotiatedOnlyAnExplicitFormatComplaintRetries()
    {
        // The bound that stops a wrong model name causing a repeated large re-upload on every job.
        var opaque = new System.Net.Http.HttpRequestException(
            "boom", null, HttpStatusCode.BadRequest);
        Assert.False(
            RemoteWhisperProvider.ShouldNegotiateAlternateFormat(opaque, formatNotYetNegotiated: false));

        var explicitComplaint = new System.Net.Http.HttpRequestException(
            "response_format srt is not supported", null, HttpStatusCode.BadRequest);
        Assert.True(
            RemoteWhisperProvider.ShouldNegotiateAlternateFormat(explicitComplaint, formatNotYetNegotiated: false));
    }

    // ---- Validation edges --------------------------------------------------------------------------

    [Fact]
    public void NullWorkerIsRejected()
    {
        var (ok, error) = WorkerConfigValidation.Validate(null!);
        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void DuplicateEndpointCheckToleratesNull()
    {
        Assert.Empty(WorkerConfigValidation.CheckDuplicateEndpoints(null));
    }

    [Fact]
    public void BlankUploadCodecIsAccepted()
    {
        // Blank means "unset" and is normalized to wav elsewhere; it must not fail validation.
        var worker = new WhisperWorker { ApiUrl = "http://box:9010", UploadCodec = "" };
        var (ok, _) = WorkerConfigValidation.Validate(worker);
        Assert.True(ok);
    }

    // ---- 4.5.0.1 review findings -----------------------------------------------------------------

    [Fact]
    public void LegitimateUrlPathIsNotRedactedAway()
    {
        // '/' used to be in the high-entropy class, so any long URL path matched and the endpoint the admin
        // needs to read was replaced by [redacted].
        const string body = "upstream https://gw.example.com/v1/abcd1234abcd/audio/transcriptions failed";
        var sanitized = UpstreamErrorSanitizer.Sanitize(body, apiKey: null);

        Assert.Contains("gw.example.com", sanitized, System.StringComparison.Ordinal);
        Assert.Contains("/audio/transcriptions", sanitized, System.StringComparison.Ordinal);
    }

    [Fact]
    public void HtmlWithoutContentTypeIsStillClassifiedNotQuoted()
    {
        // Proxies and load balancers answer failures with an HTML page and often no Content-Type at all,
        // which slipped straight through the media-type allow-list.
        var detail = RemoteWhisperProvider.DescribeUpstreamErrorBody(
            "<html><body><script>alert(1)</script>502 Bad Gateway</body></html>", null, mediaType: null);

        Assert.DoesNotContain("<script", detail, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("502 Bad Gateway", detail, System.StringComparison.Ordinal);
        Assert.Contains("web page", detail, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GuidanceComesBeforeTheUpstreamDetail()
    {
        // The queue's last-error is truncated in the UI. With the advice appended last, the admin saw the
        // diagnosis and never the fix.
        var message = RemoteWhisperProvider.BuildRemoteApiMessage(
            HttpStatusCode.RequestEntityTooLarge, new string('d', 300));

        var guidanceAt = message.IndexOf("Max upload size", System.StringComparison.OrdinalIgnoreCase);
        var detailAt = message.IndexOf("endpoint said", System.StringComparison.OrdinalIgnoreCase);

        Assert.True(guidanceAt > 0, "guidance must be present");
        Assert.True(detailAt > guidanceAt, "guidance must precede the upstream detail");
        Assert.True(guidanceAt < 160, "guidance must survive a 160-character truncation");
    }
}
