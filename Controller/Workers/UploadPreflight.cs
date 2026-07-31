namespace WhisperSubs.Controller.Workers
{
    /// <summary>
    /// Decides whether an upload can possibly succeed BEFORE spending the bandwidth (issue #138).
    /// <para>
    /// The plugin knows the exact byte count before a single byte goes on the wire, so eating an HTTP 413
    /// after pushing 77 MB is pure waste — and on a metered connection or a rate-limited provider it costs
    /// real money and quota. Failing fast also lets us say something the raw 413 never could: how big the
    /// audio actually is, what the endpoint accepts, and roughly how much audio that allows.
    /// </para>
    /// </summary>
    public static class UploadPreflight
    {
        /// <summary>
        /// Whether an upload of <paramref name="uploadBytes"/> is permitted for a worker whose configured
        /// cap is <paramref name="maxUploadBytes"/>.
        /// <para>
        /// A cap of 0 (the default) means "unlimited" and ALWAYS allows the upload — this is what keeps the
        /// change byte-identical for every existing install, including self-hosted workers that have no
        /// limit at all. There is deliberately no non-zero default: real caps span 440x (Groq free 25 MB,
        /// Groq 25 MB on both tiers for direct upload, this project's own worker 8 GiB), so any guess would
        /// block working setups.
        /// </para>
        /// </summary>
        public static bool IsAllowed(long uploadBytes, long maxUploadBytes)
            => maxUploadBytes <= 0 || uploadBytes <= maxUploadBytes;

        /// <summary>
        /// Explains a blocked upload in the admin's own terms, including what to change. Returns an empty
        /// string when the upload is allowed.
        /// </summary>
        /// <param name="sourceAudioBytes">Size of the extracted uncompressed WAV (drives the duration figure).</param>
        /// <param name="uploadBytes">Size actually destined for the wire (may be a compressed re-encode).</param>
        /// <param name="maxUploadBytes">The worker's configured cap; 0 = unlimited.</param>
        /// <param name="codec">The worker's configured upload codec, for tailored advice.</param>
        public static string ExplainIfBlocked(
            long sourceAudioBytes, long uploadBytes, long maxUploadBytes, string? codec)
        {
            if (IsAllowed(uploadBytes, maxUploadBytes))
            {
                return string.Empty;
            }

            var message = RemoteErrorGuidance.DescribeOversizedUpload(sourceAudioBytes, maxUploadBytes);

            // Tailor the advice to what is actually still available to this worker.
            message += RemoteUploadFormat.Normalize(codec) switch
            {
                RemoteUploadFormat.Opus =>
                    " Already using the smallest upload format; this title is too long for this endpoint —"
                    + " use a self-hosted worker, which has no limit.",
                RemoteUploadFormat.Flac =>
                    " Switch this worker's upload format to Opus (about a tenth the size of the original"
                    + " audio), or use a self-hosted worker.",
                _ =>
                    " Set this worker's upload format to FLAC (about half) or Opus (about a tenth), or use"
                    + " a self-hosted worker, which has no limit.",
            };

            return message;
        }
    }
}
