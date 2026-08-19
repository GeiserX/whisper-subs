using System.Net;
using WhisperSubs.Controller.Workers;
using WhisperSubs.Providers;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Security tests for the boundary at which an untrusted upstream body may reach an admin's screen or the
/// Jellyfin log (issue #138). The body may contain the admin's own API key (gateways that echo the request),
/// presigned URLs, control characters (log forging) or markup — none of which may survive.
/// </summary>
public class UpstreamErrorSanitizerTests
{
    // ---- the admin's own key: the highest-value rule -------------------------------------------

    [Fact]
    public void ExactApiKeyIsRedacted()
    {
        const string key = "sk-or-v1-abcdef0123456789abcdef";
        var sanitized = UpstreamErrorSanitizer.Sanitize($"bad request for key {key} here", key);
        Assert.DoesNotContain(key, sanitized, System.StringComparison.Ordinal);
        Assert.Contains("[redacted]", sanitized, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ApiKeyIsRedactedCaseInsensitivelyAndUrlEncoded()
    {
        const string key = "MySecretKeyValue123";
        var sanitized = UpstreamErrorSanitizer.Sanitize(
            "seen mysecretkeyvalue123 and MySecretKeyValue123 in url", key);
        Assert.DoesNotContain("mysecretkeyvalue123", sanitized, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShortKeyDoesNotBlankTheMessage()
    {
        // A short/placeholder key must not turn the whole body into [redacted] noise.
        var sanitized = UpstreamErrorSanitizer.Sanitize("model 'abc' not found", "abc");
        Assert.Contains("not found", sanitized, System.StringComparison.Ordinal);
    }

    // ---- secret families ------------------------------------------------------------------------

    [Theory]
    [InlineData("Authorization: Bearer sk-proj-AAAAAAAAAAAAAAAAAAAA")]
    [InlineData("bearer abcdefghijklmnopqrstuvwx")]
    [InlineData("gsk_abcdefghijklmnopqrstuvwxyz012345")]
    [InlineData("\"api_key\": \"supersecretvalue\"")]
    [InlineData("sig=0123456789abcdef0123456789abcdef")]
    public void SecretFamiliesAreRedacted(string body)
    {
        var sanitized = UpstreamErrorSanitizer.Sanitize(body, apiKey: null);
        Assert.Contains("redacted", sanitized, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void JwtIsRedacted()
    {
        // Assembled at runtime rather than written as a literal: a whole JWT in source trips secret
        // scanners (GitGuardian flags it) even though it is a throwaway fixture.
        var jwt = string.Join(".",
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9",
            "eyJzdWIiOiIxMjM0NTY3ODkwIn0",
            "dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1gFWFOEjXk");

        var sanitized = UpstreamErrorSanitizer.Sanitize($"token {jwt} rejected", apiKey: null);

        Assert.DoesNotContain(jwt, sanitized, System.StringComparison.Ordinal);
        Assert.Contains("redacted", sanitized, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UrlQueryStringIsDroppedWholesale()
    {
        var sanitized = UpstreamErrorSanitizer.Sanitize(
            "fetch https://bucket.s3.amazonaws.com/f.wav?X-Amz-Signature=deadbeefdeadbeefdeadbeef failed",
            apiKey: null);
        Assert.DoesNotContain("X-Amz-Signature", sanitized, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("https://bucket.s3.amazonaws.com/f.wav", sanitized, System.StringComparison.Ordinal);
    }

    [Fact]
    public void EmailIsMasked()
    {
        var sanitized = UpstreamErrorSanitizer.Sanitize("account someone@example.com over quota", null);
        Assert.DoesNotContain("someone@example.com", sanitized, System.StringComparison.Ordinal);
    }

    // ---- flattening: log forging + markup -------------------------------------------------------

    [Fact]
    public void ControlCharactersAreStripped()
    {
        var sanitized = UpstreamErrorSanitizer.Sanitize(
            "line one\r\n2026-01-01 FAKE LOG LINE\tinjected​‮", null);
        Assert.DoesNotContain("\n", sanitized, System.StringComparison.Ordinal);
        Assert.DoesNotContain("\r", sanitized, System.StringComparison.Ordinal);
        Assert.DoesNotContain("\t", sanitized, System.StringComparison.Ordinal);
        Assert.DoesNotContain("​", sanitized, System.StringComparison.Ordinal);
        Assert.DoesNotContain("‮", sanitized, System.StringComparison.Ordinal);
    }

    // ---- ordering: redact BEFORE truncate -------------------------------------------------------

    [Fact]
    public void RedactionHappensBeforeTruncationSoNoPartialSecretSurvives()
    {
        const string key = "sk-live-0123456789abcdefghijklmnop";
        var body = new string('x', 380) + " " + key;
        var sanitized = UpstreamErrorSanitizer.Sanitize(body, key, maxLength: 400);

        // The key straddles the cut; if truncation ran first a prefix would leak.
        Assert.DoesNotContain("sk-live-0123", sanitized, System.StringComparison.Ordinal);
        Assert.True(sanitized.Length <= 400);
    }

    [Fact]
    public void OutputIsCapped()
    {
        var sanitized = UpstreamErrorSanitizer.Sanitize(new string('a', 5000), null, maxLength: 120);
        Assert.True(sanitized.Length <= 120);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankInputYieldsEmpty(string? body)
    {
        Assert.Equal(string.Empty, UpstreamErrorSanitizer.Sanitize(body, "somekeyvalue"));
    }

    // ---- SanitizeEndpoint ------------------------------------------------------------------------

    [Fact]
    public void SanitizeEndpointDropsUserInfoAndQuery()
    {
        var sanitized = UpstreamErrorSanitizer.SanitizeEndpoint(
            "https://user:pass@api.example.com/v1/audio/transcriptions?api_key=supersecret");
        Assert.DoesNotContain("pass", sanitized, System.StringComparison.Ordinal);
        Assert.DoesNotContain("supersecret", sanitized, System.StringComparison.Ordinal);
        Assert.Contains("api.example.com/v1/audio/transcriptions", sanitized, System.StringComparison.Ordinal);
    }

    [Fact]
    public void SanitizeEndpointKeepsAPlainUrlIntact()
    {
        const string url = "https://api.groq.com/openai/v1/audio/transcriptions";
        Assert.Equal(url, UpstreamErrorSanitizer.SanitizeEndpoint(url));
    }

    // ---- media-type gating (what may be echoed at all) -------------------------------------------

    [Theory]
    [InlineData(null, true)]
    [InlineData("application/json", true)]
    [InlineData("application/problem+json", true)]
    [InlineData("text/plain", true)]
    [InlineData("text/html", false)]
    [InlineData("application/octet-stream", false)]
    public void OnlySafeMediaTypesMayBeEchoed(string? mediaType, bool expected)
    {
        Assert.Equal(expected, RemoteWhisperProvider.MaySnippetUpstreamBody(mediaType));
    }

    [Fact]
    public void HtmlBodyIsClassifiedNotQuoted()
    {
        var detail = RemoteWhisperProvider.DescribeUpstreamErrorBody(
            "<html><body>Login page<script>alert(1)</script></body></html>", null, "text/html");
        Assert.DoesNotContain("<script", detail, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Login page", detail, System.StringComparison.Ordinal);
        Assert.Contains("web page", detail, System.StringComparison.OrdinalIgnoreCase);
    }

    // ---- the assembled message -------------------------------------------------------------------

    [Fact]
    public void MessageCarriesStatusDetailAndGuidance()
    {
        var message = RemoteWhisperProvider.BuildRemoteApiMessage(
            HttpStatusCode.RequestEntityTooLarge, "file too large");
        Assert.Contains("413", message, System.StringComparison.Ordinal);
        Assert.Contains("file too large", message, System.StringComparison.Ordinal);
        Assert.Contains("Max upload size", message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MessageWithoutDetailStillCarriesGuidance()
    {
        var message = RemoteWhisperProvider.BuildRemoteApiMessage(HttpStatusCode.Unauthorized, "");
        Assert.Contains("401", message, System.StringComparison.Ordinal);
        Assert.Contains("API key", message, System.StringComparison.OrdinalIgnoreCase);
    }

    // ---- redirect answers (#157) ------------------------------------------------------------------
    // A redirect's body is an empty stub; its Location header is the diagnosis. But Location is
    // upstream-controlled, and an SSO gateway's target routinely embeds the ORIGINAL request URL in its
    // query (rd=/redirect_uri=) — where an admin may have pasted a key — so it crosses the same boundary
    // as any upstream text: sanitized, never raw.

    [Fact]
    public void RedirectTarget_AbsoluteWithSecretQuery_KeepsHostDropsQuery()
    {
        var target = UpstreamErrorSanitizer.SanitizeRedirectTarget(
            new System.Uri("https://auth.example.com/login?rd=https%3A%2F%2Fapi.example.com%2Fv1%3Fapi_key%3Dsk-secret123456"));

        Assert.Contains("auth.example.com/login", target, System.StringComparison.Ordinal);
        Assert.DoesNotContain("sk-secret123456", target, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RedirectTarget_RelativeForm_Survives()
    {
        // Gateways may emit a relative Location ("/login"); it still names the page and carries no host.
        var target = UpstreamErrorSanitizer.SanitizeRedirectTarget(new System.Uri("/login", System.UriKind.Relative));

        Assert.Contains("/login", target, System.StringComparison.Ordinal);
    }

    [Fact]
    public void RedirectTarget_RelativeQuery_IsDroppedEvenWhenOpaque()
    {
        // An SSO "state" value is opaque — it matches no secret pattern, yet may embed the original
        // request URL with a key in it. The relative form must lose its whole query, same as the
        // absolute form does, and keep the "?[redacted]" marker so the admin knows one was there.
        var target = UpstreamErrorSanitizer.SanitizeRedirectTarget(
            new System.Uri("/login?state=b64.opaque_blob-with-secrets&rd=%2Fv1%3Fapi_key%3Dsk-abc", System.UriKind.Relative));

        Assert.StartsWith("/login?[redacted]", target, System.StringComparison.Ordinal);
        Assert.DoesNotContain("state=", target, System.StringComparison.Ordinal);
        Assert.DoesNotContain("sk-abc", target, System.StringComparison.Ordinal);
    }

    [Fact]
    public void RedirectTarget_RelativeFragment_IsDropped()
    {
        // SPA-style gateways put the route after '#': "/portal#/auth?token=x". Nothing after the path
        // is worth the risk; the path alone names the page.
        var target = UpstreamErrorSanitizer.SanitizeRedirectTarget(
            new System.Uri("/portal#/auth?token=xyz123", System.UriKind.Relative));

        Assert.Equal("/portal", target);
    }

    [Fact]
    public void RedirectTarget_MissingLocation_IsEmpty()
    {
        Assert.Equal(string.Empty, UpstreamErrorSanitizer.SanitizeRedirectTarget(null));
    }

    [Fact]
    public void RedirectDetail_NamesWhereTheCallWasSent()
    {
        var detail = RemoteWhisperProvider.DescribeRedirectDetail(new System.Uri("https://auth.example.com/login"));

        Assert.Contains("redirected this call to", detail, System.StringComparison.Ordinal);
        Assert.Contains("https://auth.example.com/login", detail, System.StringComparison.Ordinal);
    }

    [Fact]
    public void RedirectDetail_MissingLocation_IsEmpty_SoTheBodyPathStillApplies()
    {
        Assert.Equal(string.Empty, RemoteWhisperProvider.DescribeRedirectDetail(null));
    }

    [Fact]
    public void AssembledRedirectMessage_CarriesStatusGuidanceAndTarget()
    {
        // The full #157 admin experience in one string: what happened (302), why it usually happens
        // (a gateway intercepting the URL), and where the call was actually sent.
        var message = RemoteWhisperProvider.BuildRemoteApiMessage(
            HttpStatusCode.Found,
            RemoteWhisperProvider.DescribeRedirectDetail(new System.Uri("https://auth.example.com/login")));

        Assert.Contains("302", message, System.StringComparison.Ordinal);
        Assert.Contains("auth/SSO gateway", message, System.StringComparison.Ordinal);
        Assert.Contains("https://auth.example.com/login", message, System.StringComparison.Ordinal);
    }
}
