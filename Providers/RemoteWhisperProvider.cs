using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WhisperSubs.Controller;

namespace WhisperSubs.Providers
{
    public class RemoteWhisperProvider : ISubtitleProvider
    {
        private static readonly HttpClient _httpClient = new(new SocketsHttpHandler
        {
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

        public string Name => "RemoteWhisper";

        /// <summary>Remote API returns its own timestamps; no local VAD involvement.</summary>
        public bool UsesVad => false;

        [ExcludeFromCodeCoverage(Justification = "Construction + HTTPS-key warning; no unit-testable logic")]
        public RemoteWhisperProvider(ILogger logger, string apiUrl, string model, string apiKey = "",
            double realtimeFactor = 6.0, int minTimeoutSeconds = 60, int maxTimeoutHours = 12)
        {
            _logger = logger;
            _apiUrl = apiUrl.TrimEnd('/');
            _model = model;
            _apiKey = (apiKey ?? string.Empty).Trim();
            _realtimeFactor = realtimeFactor;
            _minTimeoutSeconds = minTimeoutSeconds;
            _maxTimeoutHours = maxTimeoutHours;

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

            _logger.LogInformation("Sending audio to remote Whisper API: {Endpoint} [lang={Language}, translate={Translate}]",
                endpoint, language, translate);

            long audioBytes = new FileInfo(audioPath).Length;

            using var content = new MultipartFormDataContent();
            // Stream the WAV rather than File.ReadAllBytes: a 2h film is ~260 MB, and N parallel workers
            // buffering that in RAM is a direct OOM path. StreamContent's file stream is disposed with the
            // MultipartFormDataContent.
            var fileContent = new StreamContent(File.OpenRead(audioPath));
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            content.Add(fileContent, "file", "audio.wav");

            content.Add(new StringContent(_model), "model");
            content.Add(new StringContent("srt"), "response_format");

            if (!string.IsNullOrWhiteSpace(language) &&
                !string.Equals(language, "auto", StringComparison.OrdinalIgnoreCase))
            {
                content.Add(new StringContent(language), "language");
            }

            var srt = await PostAudioAsync(endpoint, content, audioBytes, cancellationToken).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(srt))
            {
                throw new InvalidOperationException("Remote Whisper API returned empty response");
            }

            _logger.LogInformation("Remote transcription complete, received {Length} characters of SRT", srt.Length);
            return srt;
        }

        [ExcludeFromCodeCoverage(Justification = "HTTP I/O; the pure deadline policy is tested in TranscriptionTimeoutTests")]
        public async Task<(string Language, float Probability)> DetectLanguageAsync(string audioPath, CancellationToken cancellationToken)
        {
            if (!File.Exists(audioPath))
            {
                throw new FileNotFoundException($"Audio file not found: {audioPath}");
            }

            _logger.LogInformation("Detecting language via remote Whisper API for {AudioPath}", audioPath);

            long audioBytes = new FileInfo(audioPath).Length;

            using var content = new MultipartFormDataContent();
            var fileContent = new StreamContent(File.OpenRead(audioPath));
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            content.Add(fileContent, "file", "audio.wav");

            content.Add(new StringContent(_model), "model");
            content.Add(new StringContent("verbose_json"), "response_format");

            var endpoint = $"{_apiUrl}/v1/audio/transcriptions";
            var json = await PostAudioAsync(endpoint, content, audioBytes, cancellationToken).ConfigureAwait(false);

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
        private async Task<string> PostAudioAsync(string endpoint, MultipartFormDataContent content, long audioBytes, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = content };
            ApplyAuthorization(request);

            var deadline = TranscriptionTimeout.Compute(audioBytes, _realtimeFactor, _minTimeoutSeconds, _maxTimeoutHours);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(deadline);

            try
            {
                using var response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    var errorBody = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
                    throw new HttpRequestException($"Remote Whisper API returned {(int)response.StatusCode}: {errorBody}");
                }

                return await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Remote Whisper API call exceeded its {deadline.TotalSeconds:F0}s deadline (endpoint slow or unreachable): {endpoint}");
            }
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
