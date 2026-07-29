using System.IO;
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

    [Fact]
    public async Task TruncationWritesThePartialBufferWhenTheLimitIsNotBufferAligned()
    {
        // maxBytes > the 8 KB read buffer, so the overflowing read must contribute its first N bytes
        // rather than being dropped whole — otherwise the snippet would silently lose up to 8 KB.
        var content = Content(new string('z', 40_000));

        var read = await RemoteWhisperProvider.ReadUtf8BoundedAsync(
            content, maxBytes: 10_000, CancellationToken.None, truncate: true);

        Assert.Equal(10_000, read.Length);
    }

    [Fact]
    public async Task UndeclaredLengthTranscriptStillThrowsWhenItOverflows()
    {
        // No Content-Length (a chunked/streamed response), so the declared-length short-circuit cannot
        // fire and the in-loop guard is what has to stop an unbounded transcript read.
        var content = new StreamContent(new NonSeekableStream(
            System.Text.Encoding.UTF8.GetBytes(new string('s', 40_000))));
        Assert.Null(content.Headers.ContentLength);

        await Assert.ThrowsAsync<System.InvalidOperationException>(
            () => RemoteWhisperProvider.ReadUtf8BoundedAsync(
                content, maxBytes: 10_000, CancellationToken.None));
    }

    /// <summary>A forward-only stream, so StreamContent cannot infer a Content-Length from it.</summary>
    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new System.NotSupportedException();

        public override long Position
        {
            get => throw new System.NotSupportedException();
            set => throw new System.NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override void Flush() => _inner.Flush();

        public override long Seek(long offset, SeekOrigin origin) => throw new System.NotSupportedException();

        public override void SetLength(long value) => throw new System.NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new System.NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
