using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using WhisperSubs.Providers;
using Xunit;

namespace WhisperSubs.Tests;

/// <summary>
/// Covers the error-body read policy added in 4.5.0.1.
/// <para>
/// An endpoint behind a proxy answers a failure with a multi-KB HTML page. Throwing on that lost the status
/// code, the guidance AND the format negotiation — the thrown type is not <see cref="HttpRequestException"/>,
/// so the negotiation catch never ran. Error bodies therefore truncate. A TRANSCRIPT must still throw:
/// silently truncating one would ship subtitles quietly missing their tail.
/// </para>
/// </summary>
public class BoundedResponseReadTests
{
    private static HttpContent Content(string body) => new StringContent(body);

    [Fact]
    public async Task OversizedErrorBodyIsTruncatedNotThrown()
    {
        var huge = new string('e', 20_000);

        var read = await RemoteWhisperProvider.ReadUtf8BoundedAsync(
            Content(huge), maxBytes: 4096, CancellationToken.None, truncate: true);

        Assert.Equal(4096, read.Length);
    }

    [Fact]
    public async Task OversizedTranscriptStillThrows()
    {
        var huge = new string('t', 20_000);

        await Assert.ThrowsAsync<System.InvalidOperationException>(
            () => RemoteWhisperProvider.ReadUtf8BoundedAsync(
                Content(huge), maxBytes: 4096, CancellationToken.None));
    }

    [Fact]
    public async Task DeclaredOversizeLengthIsAlsoTruncatedWhenTruncating()
    {
        // StringContent sets Content-Length, so this exercises the declared-length short-circuit too.
        var content = Content(new string('x', 10_000));
        Assert.NotNull(content.Headers.ContentLength);

        var read = await RemoteWhisperProvider.ReadUtf8BoundedAsync(
            content, maxBytes: 512, CancellationToken.None, truncate: true);

        Assert.Equal(512, read.Length);
    }

    [Fact]
    public async Task BodyUnderTheLimitIsReturnedWhole()
    {
        const string body = """{"error":"model does not exist"}""";

        var truncating = await RemoteWhisperProvider.ReadUtf8BoundedAsync(
            Content(body), maxBytes: 4096, CancellationToken.None, truncate: true);
        var strict = await RemoteWhisperProvider.ReadUtf8BoundedAsync(
            Content(body), maxBytes: 4096, CancellationToken.None);

        Assert.Equal(body, truncating);
        Assert.Equal(body, strict);
    }

    [Fact]
    public async Task TruncatedErrorBodyStillFeedsTheFormatNegotiation()
    {
        // The point of truncating: a huge body that happens to mention the format must still be matchable,
        // rather than blowing up and skipping negotiation entirely.
        var body = """{"error":"response_format srt is not supported"}""" + new string(' ', 20_000);

        var read = await RemoteWhisperProvider.ReadUtf8BoundedAsync(
            Content(body), maxBytes: 4096, CancellationToken.None, truncate: true);

        Assert.True(RemoteWhisperProvider.IsResponseFormatRejection(
            System.Net.HttpStatusCode.BadRequest, read));
    }
}
