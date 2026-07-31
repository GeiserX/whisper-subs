using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WhisperSubs.Controller;
using WhisperSubs.Controller.Workers;

namespace WhisperSubs.Providers
{
    public class RemoteWhisperProvider : ISubtitleProvider
    {
        private sealed class RemoteApiException(HttpStatusCode statusCode, string responseBody, string detail)
            : HttpRequestException(
                BuildRemoteApiMessage(statusCode, detail),
                inner: null,
                statusCode)
        {
            /// <summary>
            /// The RAW upstream body. Kept verbatim because the response-format negotiation pattern-matches
            /// against it; sanitizing here would regress that. SECURITY-REVIEW: never surface this directly —
            /// everything user-visible goes through <see cref="UpstreamErrorSanitizer"/> (see Message).
            /// </summary>
            public string ResponseBody { get; } = responseBody;
        }

        /// <summary>
        /// Builds the admin-visible message for an upstream failure: the status, the provider's own
        /// (already sanitized) explanation when we are allowed to echo it, and one line of actionable advice.
        /// Before this, the message was a bare "returned HTTP 413" and the provider's explanation was
        /// discarded — the root reason issue #138 was undiagnosable without debug logging.
        /// </summary>
        internal static string BuildRemoteApiMessage(HttpStatusCode statusCode, string? detail)
        {
            var message = $"Remote Whisper API returned HTTP {(int)statusCode}";

            // Guidance FIRST: this message is surfaced through the queue's last-error, which the config page
            // truncates. Appending the advice after up to 400 characters of upstream detail meant the admin
            // reliably saw the diagnosis and never the fix — the opposite of the point.
            var guidance = RemoteErrorGuidance.For(statusCode);
            if (!string.IsNullOrWhiteSpace(guidance))
            {
                message += $" — {guidance}";
            }

            if (!string.IsNullOrWhiteSpace(detail))
            {
                message += $" [endpoint said: {detail}]";
            }

            return message;
        }

        /// <summary>
        /// Whether an upstream ERROR body may be echoed to the admin, by media type.
        /// SECURITY-REVIEW: <c>text/html</c> is refused deliberately — a LAN web UI answers HTML, and
        /// echoing it would both leak page content and carry markup into the UI; the classification
        /// ("this is a web page, not an API") is the better diagnostic anyway. A 2xx body is never passed
        /// here at all: it is the media transcript, i.e. the user's own content.
        /// </summary>
        internal static bool MaySnippetUpstreamBody(string? mediaType)
            => string.IsNullOrWhiteSpace(mediaType)
                || mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
                || mediaType.Equals("application/problem+json", StringComparison.OrdinalIgnoreCase)
                || mediaType.Equals("text/plain", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The admin-safe description of an upstream error body: a sanitized snippet when the media type
        /// allows it, an explicit classification for HTML, and nothing at all otherwise.
        /// </summary>
        internal static string DescribeUpstreamErrorBody(string? body, string? apiKey, string? mediaType)
        {
            // Classify by CONTENT as well as header: proxies and load balancers routinely return an HTML
            // error page with no Content-Type at all, which would otherwise sail through the allow-list.
            var trimmed = (body ?? string.Empty).TrimStart();
            if ((!string.IsNullOrWhiteSpace(mediaType)
                    && mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase))
                || trimmed.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("<", StringComparison.Ordinal))
            {
                return "the endpoint returned a web page, not an API response";
            }

            return MaySnippetUpstreamBody(mediaType)
                ? UpstreamErrorSanitizer.Sanitize(body, apiKey)
                : string.Empty;
        }

        internal const int MaxTranscriptionResponseBytes = 32 * 1024 * 1024;
        internal const int MaxErrorResponseBytes = 4096;
        private static readonly Regex SrtTimingLineRegex = new(
            @"(?m)^\s*(\d{2}):(\d{2}):(\d{2}),(\d{3})\s*-->\s*(\d{2}):(\d{2}):(\d{2}),(\d{3})\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly HttpClient SharedHttpClient = new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectTimeout = TimeSpan.FromSeconds(10),
        })
        {
            // Per-call deadlines (TranscriptionTimeout, applied via a linked CancellationTokenSource) are
            // the authority — so a long-but-healthy transcription is never guillotined and a hung call is
            // still bounded. The old fixed 30-minute timeout was the root of the multi-day stuck-task bug.
            Timeout = Timeout.InfiniteTimeSpan,
        };

        private readonly ILogger _logger;
        private readonly string _apiUrl;
        private readonly string _model;
        private readonly string _apiKey;
        private readonly double _realtimeFactor;
        private readonly int _minTimeoutSeconds;
        private readonly int _maxTimeoutHours;
        private readonly HttpClient _httpClient;
        private readonly long _maxUploadBytes;
        private readonly string _uploadCodec;
        // Set once a blind (body-didn't-say-so) format retry has been spent for this provider instance.
        // The cached format only fills on SUCCESS, so without this a worker that always 4xxs — e.g. the bare
        // OpenRouter model slug this release's README warns about — would upload the full audio twice on
        // EVERY job, and the queue retries each item JobMaxRetries times on top of that.
        private int _blindNegotiationSpent;
        private string? _transcriptionResponseFormat;
        private string? _translationResponseFormat;

        public string Name => "RemoteWhisper";

        /// <summary>
        /// Remote endpoints own their timestamping and may already use VAD. Treat their output like
        /// VAD-aligned output so the local FFmpeg correction remains opt-in through
        /// AlignSubtitlesToSpeechWithVad instead of silently re-timing every remote subtitle.
        /// </summary>
        public bool RequiresSpeechAlignmentOptIn => true;

        [ExcludeFromCodeCoverage(Justification = "Construction + HTTPS-key warning; no unit-testable logic")]
        public RemoteWhisperProvider(ILogger logger, string apiUrl, string model, string apiKey = "",
            double realtimeFactor = 6.0, int minTimeoutSeconds = 60, int maxTimeoutHours = 12,
            HttpClient? httpClient = null, long maxUploadBytes = 0, string? uploadCodec = null)
        {
            _logger = logger;
            _apiUrl = apiUrl.TrimEnd('/');
            _model = model;
            _apiKey = (apiKey ?? string.Empty).Trim();
            _realtimeFactor = realtimeFactor;
            _minTimeoutSeconds = minTimeoutSeconds;
            _maxTimeoutHours = maxTimeoutHours;
            _httpClient = httpClient ?? SharedHttpClient;
            // 0 = unlimited and "wav" = no re-encode: the defaults reproduce pre-existing behaviour exactly,
            // so an unconfigured or self-hosted worker is untouched.
            _maxUploadBytes = maxUploadBytes;
            _uploadCodec = RemoteUploadFormat.Normalize(uploadCodec);

            if (!string.IsNullOrEmpty(_apiKey) &&
                Uri.TryCreate(_apiUrl, UriKind.Absolute, out var uri) &&
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "RemoteWhisper API key is configured with a non-HTTPS URL ({Scheme}). The key will be sent in cleartext. Consider switching to https://.",
                    uri.Scheme);
            }
        }

        private void ApplyAuthorization(HttpRequestMessage request)
        {
            if (!string.IsNullOrWhiteSpace(_apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            }
        }

        [ExcludeFromCodeCoverage(Justification = "HTTP I/O; the pure deadline policy is tested in TranscriptionTimeoutTests")]
        public async Task<string> TranscribeAsync(string audioPath, string language, CancellationToken cancellationToken, bool translate = false)
        {
            if (!File.Exists(audioPath))
            {
                throw new FileNotFoundException($"Audio file not found: {audioPath}");
            }

            var endpoint = translate
                ? $"{_apiUrl}/v1/audio/translations"
                : $"{_apiUrl}/v1/audio/transcriptions";

            // SanitizeEndpoint, not the raw endpoint: an admin may legitimately paste a key into the URL
            // (Azure-style "?api-version=", proxy "?api_key="), and this line lands in jellyfin.log — the
            // file users paste into public issues.
            _logger.LogInformation("Sending audio to remote Whisper API: {Endpoint} [lang={Language}, translate={Translate}]",
                UpstreamErrorSanitizer.SanitizeEndpoint(endpoint), language, translate);

            // SOURCE bytes = the extracted 16 kHz mono PCM WAV. Every duration/deadline decision must be
            // derived from THIS, never from whatever we end up uploading: the upload may be a compressed
            // re-encode (per-worker codec), and deriving duration from compressed bytes would both collapse
            // the per-call deadline and make ConvertTranscriptionResponseToSrt reject every segment past the
            // (wrongly shortened) duration as "out-of-order". Uploaded bytes are for the body only.
            var sourceAudioBytes = new FileInfo(audioPath).Length;
            // Re-encode for the wire when this worker asks for it (default wav = no-op), then refuse to
            // upload at all if the result still exceeds the worker's configured cap: a fail-fast with real
            // numbers beats pushing 77 MB to earn a bare HTTP 413.
            // effectiveCodec is what the file ACTUALLY is, which differs from _uploadCodec whenever the
            // re-encode fell back to the source WAV (no ffmpeg, encode failed, or output was not smaller).
            // Labelling WAV bytes as audio.ogg would be rejected or mis-decoded by the provider.
            var (uploadPath, uploadIsTemporary, effectiveCodec) = await RemoteAudioEncoder
                .PrepareUploadAsync(audioPath, _uploadCodec, _logger, cancellationToken).ConfigureAwait(false);
            try
            {
                var uploadBytes = new FileInfo(uploadPath).Length;
                if (!UploadPreflight.IsAllowed(uploadBytes, _maxUploadBytes))
                {
                    throw new InvalidOperationException(
                        UploadPreflight.ExplainIfBlocked(
                            sourceAudioBytes, uploadBytes, _maxUploadBytes, effectiveCodec));
                }

                return await TranscribeUploadAsync(
                    endpoint, uploadPath, sourceAudioBytes, effectiveCodec, language, translate, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (uploadIsTemporary)
                {
                    RemoteAudioEncoder.TryDelete(uploadPath);
                }
            }
        }

        [ExcludeFromCodeCoverage(Justification = "HTTP I/O; the pure deadline policy is tested in TranscriptionTimeoutTests")]
        private async Task<string> TranscribeUploadAsync(
            string endpoint,
            string audioPath,
            long sourceAudioBytes,
            string uploadCodec,
            string language,
            bool translate,
            CancellationToken cancellationToken)
        {
            var cachedResponseFormat = translate
                ? Volatile.Read(ref _translationResponseFormat)
                : Volatile.Read(ref _transcriptionResponseFormat);
            // Nothing cached yet = we have never successfully negotiated a format with this endpoint, so a
            // format-candidate rejection is worth one retry even when the body does not name the parameter
            // (OpenRouter rejects response_format=srt with a bare 400). Bounded: once the format is cached,
            // only an explicit format complaint triggers a retry again.
            var formatNotYetNegotiated =
                cachedResponseFormat is null && Volatile.Read(ref _blindNegotiationSpent) == 0;
            var responseFormat = cachedResponseFormat ?? "srt";
            var negotiatedByFormatRejection = false;
            string response;
            try
            {
                response = await PostTranscriptionAsync(
                    endpoint, audioPath, sourceAudioBytes, uploadCodec, language, responseFormat, includeLanguage: !translate, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ShouldNegotiateAlternateFormat(ex, formatNotYetNegotiated))
            {
                // Preserve existing SRT providers as the first choice. Groq/OpenRouter reject SRT, so
                // negotiate once to timestamped verbose_json and cache the successful format. If a
                // provider's capability changes later, the reverse retry also recovers automatically.
                // Spend the blind-retry budget before re-posting, so a permanently-failing endpoint cannot
                // double every future upload.
                if (formatNotYetNegotiated && !IsResponseFormatRejection(ex))
                {
                    Interlocked.Exchange(ref _blindNegotiationSpent, 1);
                }

                responseFormat = AlternateResponseFormat(responseFormat);
                negotiatedByFormatRejection = true;
                // Information, not Warning: this is expected, successful, self-healing behavior on Groq and
                // OpenRouter. Warning on every job trains admins to ignore warnings — which is part of why
                // the real failure in #138 went unnoticed.
                _logger.LogInformation(
                    "Remote API rejected response format; retrying with {ResponseFormat}", responseFormat);
                response = await PostTranscriptionAsync(
                    endpoint, audioPath, sourceAudioBytes, uploadCodec, language, responseFormat, includeLanguage: !translate, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            if (IsUntimedJsonResponse(response) && !negotiatedByFormatRejection)
            {
                responseFormat = AlternateResponseFormat(responseFormat);
                _logger.LogInformation(
                    "Remote API returned untimed JSON; retrying with {ResponseFormat}", responseFormat);
                response = await PostTranscriptionAsync(
                    endpoint, audioPath, sourceAudioBytes, uploadCodec, language, responseFormat, includeLanguage: !translate, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            var audioDurationSeconds = SourceAudioDurationSeconds(sourceAudioBytes);
            var srt = ConvertTranscriptionResponseToSrt(response, audioDurationSeconds);

            if (string.IsNullOrWhiteSpace(srt))
            {
                throw new InvalidOperationException("Remote Whisper API returned empty response");
            }

            if (translate)
            {
                Volatile.Write(ref _translationResponseFormat, responseFormat);
            }
            else
            {
                Volatile.Write(ref _transcriptionResponseFormat, responseFormat);
            }
            _logger.LogInformation("Remote transcription complete, received {Length} characters of SRT", srt.Length);
            return srt;
        }

        [ExcludeFromCodeCoverage(Justification = "HTTP/file I/O; response conversion is covered separately")]
        private async Task<string> PostTranscriptionAsync(
            string endpoint,
            string audioPath,
            long sourceAudioBytes,
            string uploadCodec,
            string language,
            string responseFormat,
            bool includeLanguage,
            CancellationToken cancellationToken)
        {
            using var content = new MultipartFormDataContent();
            // SECURITY-REVIEW: audioPath is an application-created temporary path; never accept a request path here.
            // Stream the WAV rather than File.ReadAllBytes: a 2h film is ~260 MB, and N parallel workers
            // buffering that in RAM is a direct OOM path. StreamContent's file stream is disposed with the
            // MultipartFormDataContent.
            var fileContent = new StreamContent(File.OpenRead(audioPath));
            // Name and media type must match the actual bytes: providers routinely sniff the format from the
            // file EXTENSION, so a FLAC body called "audio.wav" is rejected or mis-decoded.
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(RemoteUploadFormat.ContentType(uploadCodec));
            content.Add(fileContent, "file", RemoteUploadFormat.FileName(uploadCodec));
            content.Add(new StringContent(_model), "model");
            content.Add(new StringContent(responseFormat), "response_format");
            // NOTE: we deliberately do NOT send timestamp_granularities[]. verbose_json already returns
            // segment timestamps by default (segment IS the default granularity), and we only ever read
            // "segments" from the response — never "words". Sending it is therefore redundant for us, and
            // actively harmful: OpenRouter accepts the field only when the requested model happens to route
            // to an OpenAI-compatible backend and returns HTTP 400 otherwise, which made every OpenRouter
            // transcription fail regardless of format negotiation (issue #138).

            if (includeLanguage
                && !string.IsNullOrWhiteSpace(language) &&
                !string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase))
            {
                content.Add(new StringContent(language), "language");
            }

            // sourceAudioBytes (the uncompressed WAV), NOT the uploaded body length: the deadline must track
            // how long the audio actually is, not how well it compressed.
            return await PostAudioAsync(endpoint, content, sourceAudioBytes, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Audio duration implied by the SOURCE (uncompressed 16 kHz mono s16le PCM) byte count. This is the
        /// only correct input for the per-call deadline and for the segment-timestamp sanity bound: a
        /// compressed upload has the same duration but a fraction of the bytes, so deriving duration from the
        /// uploaded size would shrink the deadline and make valid late segments look "out of order".
        /// </summary>
        internal static double SourceAudioDurationSeconds(long sourceAudioBytes)
            => Math.Max(0, sourceAudioBytes) / TranscriptionTimeout.BytesPerAudioSecond;

        /// <summary>
        /// Whether to retry once with the alternate response format.
        /// <para>
        /// An explicit format complaint always qualifies. Additionally, while a worker's format is still
        /// UN-NEGOTIATED, any format-candidate status (400/406/415/422) qualifies — OpenRouter rejects
        /// <c>response_format=srt</c> with a bare 400 whose body need not name the parameter, and without
        /// this the negotiation never fires and the endpoint simply never works (issue #138).
        /// </para>
        /// <para>
        /// The cost of a wrong guess is bounded twice over: at most ONE extra upload within a job, and the
        /// blind form is spent at most once per provider instance (see _blindNegotiationSpent). A worker
        /// that always 4xxs therefore pays the double upload once, not on every job.
        /// </para>
        /// </summary>
        internal static bool ShouldNegotiateAlternateFormat(HttpRequestException exception, bool formatNotYetNegotiated)
            => IsResponseFormatRejection(exception)
                || (formatNotYetNegotiated && IsFormatRejectionCandidateStatus(exception.StatusCode));

        internal static bool IsFormatRejectionCandidateStatus(HttpStatusCode? statusCode)
            => statusCode is HttpStatusCode.BadRequest
                or HttpStatusCode.NotAcceptable
                or HttpStatusCode.UnsupportedMediaType
                or HttpStatusCode.UnprocessableEntity;

        internal static bool IsResponseFormatRejection(HttpRequestException exception)
        {
            var detail = exception is RemoteApiException remoteException
                ? remoteException.ResponseBody
                : exception.Message;
            return IsResponseFormatRejection(exception.StatusCode, detail);
        }

        internal static bool IsResponseFormatRejection(HttpStatusCode? statusCode, string detail)
            => statusCode == HttpStatusCode.NotAcceptable
                || ((statusCode is HttpStatusCode.BadRequest
                        or HttpStatusCode.UnsupportedMediaType
                        or HttpStatusCode.UnprocessableEntity)
                    && (detail.Contains("response_format", StringComparison.OrdinalIgnoreCase)
                        || detail.Contains("response format", StringComparison.OrdinalIgnoreCase)
                        || detail.Contains("verbose_json", StringComparison.OrdinalIgnoreCase)
                        || detail.Contains("\"srt\"", StringComparison.OrdinalIgnoreCase)
                        || detail.Contains("'srt'", StringComparison.OrdinalIgnoreCase)
                        || detail.Contains("format srt", StringComparison.OrdinalIgnoreCase)
                        || detail.Contains("srt format", StringComparison.OrdinalIgnoreCase)
                        || detail.Contains("srt is not supported", StringComparison.OrdinalIgnoreCase)));

        internal static bool ShouldRetryProbeAsVerbose(HttpStatusCode statusCode, string body)
            => IsResponseFormatRejection(statusCode, body)
                || ((int)statusCode is >= 200 and < 300 && IsUntimedJsonResponse(body));

        internal static string AlternateResponseFormat(string responseFormat)
            => string.Equals(responseFormat, "srt", StringComparison.Ordinal) ? "verbose_json" : "srt";

        internal static bool HasTimestampedSegmentsArray(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
            {
                return false;
            }

            var payload = response.TrimStart();
            if (payload.Length > 0 && payload[0] == '\uFEFF')
            {
                payload = payload[1..].TrimStart();
            }

            try
            {
                using var document = JsonDocument.Parse(payload);
                if (document.RootElement.ValueKind != JsonValueKind.Object
                    || !document.RootElement.TryGetProperty("segments", out var segments)
                    || segments.ValueKind != JsonValueKind.Array)
                {
                    return false;
                }
                if (segments.GetArrayLength() == 0)
                {
                    return true;
                }

                ConvertTranscriptionResponseToSrt(payload);
                return true;
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                return false;
            }
        }

        internal static bool HasEmptyTimestampedSegmentsArray(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
            {
                return false;
            }
            try
            {
                using var document = JsonDocument.Parse(response.TrimStart('\uFEFF', ' ', '\t', '\r', '\n'));
                return document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("segments", out var segments)
                    && segments.ValueKind == JsonValueKind.Array
                    && segments.GetArrayLength() == 0;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        internal static bool IsUntimedJsonResponse(string response)
        {
            if (string.IsNullOrWhiteSpace(response))
            {
                return false;
            }
            try
            {
                using var document = JsonDocument.Parse(response.TrimStart('\uFEFF', ' ', '\t', '\r', '\n'));
                return document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("text", out var text)
                    && text.ValueKind == JsonValueKind.String
                    && !document.RootElement.TryGetProperty("segments", out _);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        /// <summary>
        /// Converts an OpenAI-compatible verbose_json transcription into SRT. A server that ignores
        /// the requested format and still returns raw SRT remains supported unchanged. Plain json is
        /// deliberately rejected because its text-only response has no timings and cannot produce a
        /// synchronized subtitle without fabricating them.
        /// </summary>
        internal static string ConvertTranscriptionResponseToSrt(
            string response, double? maxDurationSeconds = null)
        {
            if (string.IsNullOrWhiteSpace(response))
            {
                return response;
            }

            var payload = response;
            var trimmed = response.AsSpan().TrimStart();
            if (trimmed.Length > 0 && trimmed[0] == '\uFEFF')
            {
                trimmed = trimmed[1..].TrimStart();
                payload = trimmed.ToString();
            }
            if (trimmed.Length == 0)
            {
                return string.Empty;
            }
            if (trimmed[0] != '{' && trimmed[0] != '[')
            {
                return NormalizeRawSrt(payload, maxDurationSeconds);
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(payload);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    "Remote Whisper API returned malformed JSON instead of timestamped subtitles", ex);
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("segments", out var segments)
                    || segments.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException(
                        "Remote Whisper API returned JSON without timestamped segments. " +
                        "Use a model/provider that supports response_format=verbose_json; plain json text cannot be synchronized.");
                }

                var output = new StringBuilder();
                var index = 1;
                var previousStart = -1d;
                foreach (var segment in segments.EnumerateArray())
                {
                    if (segment.ValueKind != JsonValueKind.Object
                        || !TryReadSeconds(segment, "start", out var start)
                        || !TryReadSeconds(segment, "end", out var end)
                        || end <= start
                        || start < previousStart
                        || (maxDurationSeconds is > 0 && end > maxDurationSeconds.Value + 5))
                    {
                        throw new InvalidOperationException(
                            "Remote Whisper API returned a segment with invalid or out-of-order timestamps");
                    }
                    previousStart = start;

                    if (!segment.TryGetProperty("text", out var textProperty)
                        || textProperty.ValueKind != JsonValueKind.String)
                    {
                        throw new InvalidOperationException(
                            "Remote Whisper API returned a timestamped segment without text");
                    }

                    var text = NormalizeCueText(textProperty.GetString());
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    var startTimestamp = FormatSrtTimestamp(start);
                    var endTimestamp = FormatSrtTimestamp(end);
                    if (startTimestamp.TotalMilliseconds >= endTimestamp.TotalMilliseconds)
                    {
                        throw new InvalidOperationException(
                            "Remote Whisper API returned a segment shorter than one millisecond");
                    }

                    if (output.Length > 0)
                    {
                        output.AppendLine();
                    }

                    output.Append(index++).AppendLine();
                    output.Append(startTimestamp.Text).Append(" --> ").Append(endTimestamp.Text).AppendLine();
                    output.AppendLine(text);
                }

                if (index == 1)
                {
                    throw new InvalidOperationException(
                        "Remote Whisper API returned no non-empty timestamped segments");
                }

                return output.ToString().TrimEnd();
            }
        }

        private static string NormalizeRawSrt(string payload, double? maxDurationSeconds)
        {
            var normalizedPayload = payload
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Trim();
            var blocks = Regex.Split(normalizedPayload, @"\n[ \t]*\n");
            var output = new StringBuilder();
            var previousStart = -1d;
            var index = 1;

            foreach (var block in blocks)
            {
                var lines = block.Split('\n');
                if (lines.Length < 3
                    || !int.TryParse(lines[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out _))
                {
                    throw new InvalidOperationException("Remote Whisper API returned malformed SRT cue structure");
                }

                var match = SrtTimingLineRegex.Match(lines[1]);
                if (!match.Success)
                {
                    throw new InvalidOperationException("Remote Whisper API returned malformed SRT timestamps");
                }

                var startHours = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                var startMinutes = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                var startSeconds = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                var startMilliseconds = int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
                var endHours = int.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture);
                var endMinutes = int.Parse(match.Groups[6].Value, CultureInfo.InvariantCulture);
                var endSeconds = int.Parse(match.Groups[7].Value, CultureInfo.InvariantCulture);
                var endMilliseconds = int.Parse(match.Groups[8].Value, CultureInfo.InvariantCulture);
                var start = (startHours * 3600d) + (startMinutes * 60d) + startSeconds + (startMilliseconds / 1000d);
                var end = (endHours * 3600d) + (endMinutes * 60d) + endSeconds + (endMilliseconds / 1000d);
                if (startMinutes >= 60
                    || startSeconds >= 60
                    || endMinutes >= 60
                    || endSeconds >= 60
                    || end <= start
                    || start < previousStart
                    || (maxDurationSeconds is > 0 && end > maxDurationSeconds.Value + 5))
                {
                    throw new InvalidOperationException("Remote Whisper API returned invalid SRT timing");
                }
                previousStart = start;

                var text = NormalizeCueText(string.Join('\n', lines.Skip(2)));
                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new InvalidOperationException("Remote Whisper API returned an SRT cue without text");
                }

                if (output.Length > 0)
                {
                    output.AppendLine();
                }
                output.Append(index++).AppendLine();
                output.Append(match.Groups[1].Value).Append(':')
                    .Append(match.Groups[2].Value).Append(':')
                    .Append(match.Groups[3].Value).Append(',')
                    .Append(match.Groups[4].Value).Append(" --> ")
                    .Append(match.Groups[5].Value).Append(':')
                    .Append(match.Groups[6].Value).Append(':')
                    .Append(match.Groups[7].Value).Append(',')
                    .Append(match.Groups[8].Value).AppendLine();
                output.AppendLine(text);
            }

            if (index == 1)
            {
                throw new InvalidOperationException("Remote Whisper API returned no SRT cues");
            }
            return output.ToString().TrimEnd();
        }

        private static bool TryReadSeconds(JsonElement segment, string propertyName, out double value)
        {
            value = 0;
            if (!segment.TryGetProperty(propertyName, out var property))
            {
                return false;
            }

            var parsed = property.ValueKind switch
            {
                JsonValueKind.Number => property.TryGetDouble(out value),
                JsonValueKind.String => double.TryParse(
                    property.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value),
                _ => false,
            };

            return parsed && double.IsFinite(value) && value >= 0;
        }

        private static string NormalizeCueText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            return string.Join(
                '\n',
                text.Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Replace('\r', '\n')
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(line => new string(line.Where(c => !char.IsControl(c) || c == '\t').ToArray()))
                    .Where(line => !string.IsNullOrWhiteSpace(line)));
        }

        private static (string Text, long TotalMilliseconds) FormatSrtTimestamp(double seconds)
        {
            const long MaxSrtMilliseconds = (100L * 60 * 60 * 1000) - 1;
            if (seconds * 1000d > MaxSrtMilliseconds)
            {
                throw new InvalidOperationException(
                    "Remote Whisper API returned a timestamp beyond the 99:59:59,999 SRT limit");
            }

            var totalMilliseconds = checked((long)Math.Round(seconds * 1000d, MidpointRounding.AwayFromZero));
            var hours = totalMilliseconds / 3_600_000;
            var minutes = (totalMilliseconds / 60_000) % 60;
            var wholeSeconds = (totalMilliseconds / 1_000) % 60;
            var milliseconds = totalMilliseconds % 1_000;
            var text = string.Create(
                CultureInfo.InvariantCulture,
                $"{hours:00}:{minutes:00}:{wholeSeconds:00},{milliseconds:000}");
            return (text, totalMilliseconds);
        }

        [ExcludeFromCodeCoverage(Justification = "HTTP I/O; the pure deadline policy is tested in TranscriptionTimeoutTests")]
        public async Task<(string Language, float Probability)> DetectLanguageAsync(string audioPath, CancellationToken cancellationToken)
        {
            if (!File.Exists(audioPath))
            {
                throw new FileNotFoundException($"Audio file not found: {audioPath}");
            }

            // Debug + file name only: a full media path at default level discloses the library layout, and
            // default-level logs get pasted into public issues (repo rule: don't log private paths).
            _logger.LogDebug("Detecting language via remote Whisper API for {AudioFile}", Path.GetFileName(audioPath));

            long sourceAudioBytes = new FileInfo(audioPath).Length;

            using var content = new MultipartFormDataContent();
            var fileContent = new StreamContent(File.OpenRead(audioPath));
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            content.Add(fileContent, "file", "audio.wav");

            content.Add(new StringContent(_model), "model");
            content.Add(new StringContent("verbose_json"), "response_format");

            var endpoint = $"{_apiUrl}/v1/audio/transcriptions";
            var json = await PostAudioAsync(endpoint, content, sourceAudioBytes, cancellationToken).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var language = root.TryGetProperty("language", out var langProp)
                ? (langProp.GetString() ?? "auto")
                : "auto";

            language = NormalizeLangName(language);

            _logger.LogInformation("Remote language detection: {Language}", language);

            return (language, 0.0f);
        }

        /// <summary>
        /// POSTs a prepared multipart body under a per-call deadline derived from the audio length
        /// (<see cref="TranscriptionTimeout"/>). A caller cancellation propagates as
        /// <see cref="OperationCanceledException"/>; the deadline elapsing surfaces as a
        /// <see cref="TimeoutException"/> so a stalled/unreachable endpoint fails fast and clearly instead
        /// of hanging (the multi-day stuck-task class of bug).
        /// </summary>
        [ExcludeFromCodeCoverage(Justification = "HTTP I/O; the pure deadline policy is tested in TranscriptionTimeoutTests")]
        // sourceAudioBytes is the UNCOMPRESSED source WAV length, which is what the deadline must scale with —
        // never the (possibly compressed) uploaded body length. See SourceAudioDurationSeconds.
        private async Task<string> PostAudioAsync(string endpoint, MultipartFormDataContent content, long sourceAudioBytes, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
            ApplyAuthorization(request);

            var deadline = TranscriptionTimeout.Compute(sourceAudioBytes, _realtimeFactor, _minTimeoutSeconds, _maxTimeoutHours);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(deadline);

            try
            {
                // SECURITY-REVIEW: the configured endpoint is untrusted. Read headers first, then
                // stream the body through explicit limits instead of HttpClient's default unbounded buffer.
                using var response = await _httpClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await ReadUtf8BoundedAsync(
                        response.Content, MaxErrorResponseBytes, timeoutCts.Token, truncate: true).ConfigureAwait(false);
                    // Echo the provider's own explanation, sanitized and media-type gated, so the admin can
                    // see WHY without turning on debug logging (#138). The raw body is retained on the
                    // exception for the response-format negotiation matcher.
                    var detail = DescribeUpstreamErrorBody(
                        errorBody, _apiKey, response.Content.Headers.ContentType?.MediaType);
                    throw new RemoteApiException(response.StatusCode, errorBody, detail);
                }

                return await ReadUtf8BoundedAsync(
                    response.Content, MaxTranscriptionResponseBytes, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // This message reaches the queue's last-error and therefore the browser — sanitize the
                // endpoint so a key pasted into the URL query cannot surface there.
                throw new TimeoutException(
                    $"Remote Whisper API call exceeded its {deadline.TotalSeconds:F0}s deadline (endpoint slow or unreachable): {UpstreamErrorSanitizer.SanitizeEndpoint(endpoint)}");
            }
        }

        /// <param name="truncate">
        /// When true, an oversized body is TRUNCATED to <paramref name="maxBytes"/> instead of throwing.
        /// Use this for ERROR bodies only: an endpoint behind a proxy answers a failure with a multi-KB HTML
        /// page, and throwing there loses the status code, the guidance and the format negotiation (the
        /// thrown type is not HttpRequestException, so the negotiation catch never runs) — which defeats the
        /// entire point of surfacing provider errors. A TRANSCRIPT must still throw: silently truncating one
        /// would produce subtitles that are quietly missing their tail.
        /// </param>
        internal static async Task<string> ReadUtf8BoundedAsync(
            HttpContent content, int maxBytes, CancellationToken cancellationToken, bool truncate = false)
        {
            if (maxBytes < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxBytes));
            }
            if (!truncate
                && content.Headers.ContentLength is long declaredLength && declaredLength > maxBytes)
            {
                throw new InvalidOperationException("Remote Whisper API response exceeded the size limit");
            }

            await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var initialCapacity = content.Headers.ContentLength is long length
                ? (int)Math.Min(Math.Min(length, 64 * 1024), maxBytes)
                : Math.Min(8192, maxBytes);
            using var output = new MemoryStream(initialCapacity);
            var buffer = new byte[Math.Min(8192, maxBytes)];
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }
                if (output.Length + read > maxBytes)
                {
                    if (!truncate)
                    {
                        throw new InvalidOperationException("Remote Whisper API response exceeded the size limit");
                    }

                    // Keep what fits and stop reading; the sanitizer caps it far shorter anyway.
                    var remaining = (int)(maxBytes - output.Length);
                    if (remaining > 0)
                    {
                        output.Write(buffer, 0, remaining);
                    }

                    break;
                }
                output.Write(buffer, 0, read);
            }

            return Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
        }

        private static string NormalizeLangName(string lang)
        {
            if (lang.Length <= 3) return lang;

            return lang.ToLowerInvariant() switch
            {
                "english" => "en",
                "spanish" => "es",
                "french" => "fr",
                "german" => "de",
                "italian" => "it",
                "portuguese" => "pt",
                "russian" => "ru",
                "japanese" => "ja",
                "chinese" => "zh",
                "korean" => "ko",
                "dutch" => "nl",
                "polish" => "pl",
                "turkish" => "tr",
                "arabic" => "ar",
                "hindi" => "hi",
                "czech" => "cs",
                "greek" => "el",
                "hungarian" => "hu",
                "romanian" => "ro",
                "swedish" => "sv",
                "danish" => "da",
                "finnish" => "fi",
                "norwegian" => "no",
                "catalan" => "ca",
                "ukrainian" => "uk",
                "vietnamese" => "vi",
                "thai" => "th",
                "indonesian" => "id",
                "malay" => "ms",
                "hebrew" => "he",
                _ => lang,
            };
        }
    }
}
