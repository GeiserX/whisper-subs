using System;
using System.Globalization;
using System.Net;

namespace WhisperSubs.Controller.Workers
{
    /// <summary>
    /// Turns an upstream HTTP failure into one actionable sentence an admin can act on without reading
    /// documentation or enabling debug logging (issue #138: the reporter saw a bare "HTTP 413"/"HTTP 400"
    /// and had no way to find out what to change). Pure and unit-tested; the caller appends the result to
    /// the error surfaced in the log, the queue's last-error, and the config page.
    /// </summary>
    public static class RemoteErrorGuidance
    {
        /// <summary>16 kHz mono s16le PCM, the format the plugin extracts and uploads by default.</summary>
        private const double BytesPerAudioSecond = 32000.0;

        /// <summary>
        /// Short, actionable advice for a status code. Empty when we have nothing useful to add beyond the
        /// upstream's own message (never invent guidance we cannot stand behind).
        /// </summary>
        public static string For(HttpStatusCode statusCode) => (int)statusCode switch
        {
            // Refused, never followed: following would re-send the whole audio upload, and a 302's
            // POST-becomes-GET rewrite silently drops the audio anyway — the exact failure mode that made
            // pre-v4.4 builds save a login page as a subtitle (issue #157).
            301 or 302 or 303 or 307 or 308 =>
                "The endpoint redirected the request instead of answering it. WhisperSubs never follows a redirect with your audio - " +
                "usually an auth/SSO gateway or an http-to-https rewrite is intercepting the URL. " +
                "Point the worker at the API's direct, final address (and exempt it from any login gateway).",
            401 => "The endpoint rejected the API key. Check it is pasted in full, belongs to this provider, and has not expired.",
            403 => "The key was accepted but this account is not allowed to use it here - check model access and billing on the provider's dashboard.",
            404 => "No transcription endpoint at that address. Enter only the BASE url (WhisperSubs appends /v1/audio/transcriptions itself), so it must not already end in /v1.",
            405 or 501 => "That address exists but does not accept audio uploads - it is probably a web page or a chat-only API, not a speech-to-text endpoint.",
            413 => "The upload was larger than this endpoint accepts. Set 'Max upload size' on this worker, switch its upload format to FLAC or Opus, or use a self-hosted worker (no limit).",
            429 => "Rate limited by the provider. Lower this worker's max concurrency, or raise its cost weight so a local worker is preferred.",
            >= 500 and <= 599 => "The endpoint failed on its own side - nothing is wrong with your settings. Retry later, or check the provider's status page.",
            _ => string.Empty,
        };

        /// <summary>
        /// Explains an oversized upload in the admin's own terms: how big the audio actually is, what the
        /// endpoint accepts, and roughly how much audio that allows. Uses the SOURCE (uncompressed) size,
        /// because that is what the duration figure must be derived from.
        /// </summary>
        public static string DescribeOversizedUpload(long sourceAudioBytes, long maxUploadBytes)
        {
            if (sourceAudioBytes <= 0 || maxUploadBytes <= 0)
            {
                return string.Empty;
            }

            var minutes = sourceAudioBytes / BytesPerAudioSecond / 60.0;
            var allowedMinutes = maxUploadBytes / BytesPerAudioSecond / 60.0;

            return string.Format(
                CultureInfo.InvariantCulture,
                "This {0:F0}-minute title is {1} of audio, but this endpoint accepts {2} (about {3:F0} minutes of uncompressed audio). Nothing was uploaded.",
                minutes,
                FormatBytes(sourceAudioBytes),
                FormatBytes(maxUploadBytes),
                allowedMinutes);
        }

        /// <summary>
        /// Human-friendly byte size (invariant culture, so logs never drift with locale). DECIMAL units, to
        /// match how providers quote their limits ("25 MB"), how the README states sizes, and how the config
        /// page converts its "max upload size (MB)" field. Using binary units here would print a different
        /// number than the docs for the same file, and would set a cap ~5% larger than the admin intended -
        /// enough to let an upload through pre-flight and still earn a real 413.
        /// </summary>
        public static string FormatBytes(long bytes)
        {
            if (bytes < 0)
            {
                bytes = 0;
            }

            return bytes >= 1_000_000_000L
                ? string.Format(CultureInfo.InvariantCulture, "{0:F1} GB", bytes / 1_000_000_000.0)
                : bytes >= 1_000_000L
                    ? string.Format(CultureInfo.InvariantCulture, "{0:F1} MB", bytes / 1_000_000.0)
                    : string.Format(CultureInfo.InvariantCulture, "{0:F0} KB", Math.Ceiling(bytes / 1000.0));
        }
    }
}
