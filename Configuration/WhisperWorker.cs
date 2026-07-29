namespace WhisperSubs.Configuration
{
    /// <summary>
    /// One configured extra transcription worker (v4.0 worker pool). This list is EMPTY for a normal
    /// single-server install — the plugin then just uses the host's own local whisper, exactly as today.
    /// A power user adds entries here to pool additional OpenAI-compatible endpoints (a second box, a NAS,
    /// a cloud API). Plain mutable class so it round-trips through the plugin's XML config.
    /// </summary>
    public class WhisperWorker
    {
        /// <summary>Stable id (dispatch/dedup key); survives a rename.</summary>
        public string Id { get; set; } = "";

        /// <summary>Display label, e.g. "nas-igpu".</summary>
        public string Name { get; set; } = "";

        public bool Enabled { get; set; } = true;

        /// <summary>OpenAI-compatible base URL, e.g. <c>http://192.168.1.10:8080</c>.</summary>
        public string ApiUrl { get; set; } = "";

        /// <summary>Optional bearer token for this worker.</summary>
        public string ApiKey { get; set; } = "";

        /// <summary>Model to request; empty = the worker's own default / any.</summary>
        public string Model { get; set; } = "";

        /// <summary>Simultaneous jobs this worker runs; keep 1 per single GPU. Default 1.</summary>
        public int MaxConcurrency { get; set; } = 1;

        /// <summary>Selection cost: 0 = free/local-priced (preferred); &gt;0 = paid, used only to burst. Default 0.</summary>
        public double CostWeight { get; set; } = 0;

        /// <summary>Whether this worker can translate to English. Default true.</summary>
        public bool CanTranslate { get; set; } = true;

        /// <summary>
        /// Largest upload this endpoint accepts, in bytes. <c>0</c> (default) means unlimited, which is the
        /// pre-existing behaviour and what every self-hosted worker wants. Set it for a hosted provider
        /// (OpenAI and Groq free tier are 25 MB; Groq dev tier 100 MB) so an oversized title fails fast with
        /// a useful message instead of a bare HTTP 413 after the whole file has been uploaded.
        /// There is deliberately no non-zero default: real caps differ by ~440x, so a guess would break
        /// working setups.
        /// </summary>
        public long MaxUploadBytes { get; set; }

        /// <summary>
        /// Audio format used when uploading to THIS worker: <c>wav</c> (default), <c>flac</c> or
        /// <c>opus</c>. The plugin always extracts 16 kHz mono PCM WAV — 1.92 MB per minute — so a
        /// 40-minute title is 76.8 MB and exceeds every hosted provider's cap. FLAC is lossless and about
        /// half the size; Opus (24 kbps mono) is about a tenth, enough for a feature film.
        /// <para>
        /// Default is <c>wav</c> ON PURPOSE: whisper.cpp's whisper-server decodes WAV only, and this
        /// project's own worker image ships without ffmpeg, so anything else would break every self-hosted
        /// worker. Only enable a compressed format on an endpoint documented to accept it.
        /// </para>
        /// </summary>
        public string UploadCodec { get; set; } = "wav";
    }
}
