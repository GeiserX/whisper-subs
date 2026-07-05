using System.Text.Json.Serialization;
using MediaBrowser.Model.Plugins;

namespace WhisperSubs.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        public string WhisperModelPath { get; set; } = "";
        public string WhisperBinaryPath { get; set; } = "";

        /// <summary>
        /// The whisper-cli binary variant that was actually downloaded and validated (e.g. "vulkan",
        /// "vulkan-noavx", "cpu", "noavx"). Persisted so the setup page re-offers the installed variant
        /// instead of silently re-defaulting the dropdown to the GPU recommendation on every load,
        /// which could overwrite a deliberately-chosen compatibility build. (Issue #95 follow-up.)
        /// </summary>
        public string WhisperBinaryVariant { get; set; } = "";

        public bool EnableAutoGeneration { get; set; } = false;

        /// <summary>
        /// Default language for subtitle generation.
        /// "auto" = detect from audio stream metadata, fall back to whisper auto-detection.
        /// Any ISO 639-1 code (e.g. "es", "en", "fr") forces that language.
        /// </summary>
        public string DefaultLanguage { get; set; } = "auto";

        /// <summary>
        /// Controls whether to generate full subtitles, forced-only subtitles, or both.
        /// </summary>
        [JsonConverter(typeof(SubtitleModeConverter))]
        public SubtitleMode SubtitleMode { get; set; } = SubtitleMode.Full;

        /// <summary>
        /// When enabled, music libraries are scanned and audio tracks receive
        /// .lrc lyrics files generated via whisper transcription.
        /// Experimental: whisper models are optimized for speech, not singing.
        /// </summary>
        public bool EnableLyricsGeneration { get; set; } = false;

        /// <summary>
        /// When enabled, generates English subtitles via whisper's --translate flag
        /// for media that lacks an English audio track.
        /// Only applies when SubtitleMode includes Full subtitles.
        /// </summary>
        public bool EnableTranslation { get; set; } = false;

        /// <summary>
        /// Number of threads for whisper.cpp inference. 0 = whisper default (4).
        /// Higher values use more CPU cores for faster transcription.
        /// </summary>
        public int WhisperThreadCount { get; set; } = 0;

        /// <summary>
        /// Optional URL of an OpenAI-compatible Whisper API server (e.g. faster-whisper-server/Speaches).
        /// When set, audio is sent to this endpoint instead of running whisper-cli locally.
        /// Example: http://192.168.1.100:8000
        /// </summary>
        public string RemoteWhisperApiUrl { get; set; } = "";

        /// <summary>
        /// Model name to request from the remote API.
        /// For Speaches/faster-whisper-server: a Hugging Face model ID (e.g. "Systran/faster-whisper-large-v3").
        /// For OpenAI: "whisper-1".
        /// </summary>
        public string RemoteWhisperModel { get; set; } = "Systran/faster-whisper-large-v3";

        /// <summary>
        /// Optional API key for the remote Whisper API. When set, the value is
        /// sent as `Authorization: Bearer &lt;key&gt;` on every request. Required by
        /// OpenAI-compatible servers that gate access (OpenAI, hosted Speaches,
        /// pfrankov/whisper-server when configured with auth, etc.). Leave
        /// empty for unauthenticated local servers.
        /// </summary>
        public string RemoteWhisperApiKey { get; set; } = "";

        /// <summary>
        /// When enabled, subtitle generation pauses while any user is actively
        /// playing media and resumes automatically when playback stops.
        /// </summary>
        public bool PauseOnPlayback { get; set; } = false;

        /// <summary>
        /// Extra arguments appended to every whisper-cli invocation (space-separated).
        /// Only applies to local whisper-cli, not the remote API.
        /// Example: --max-len 47 --split-on-word
        /// </summary>
        public string CustomWhisperArgs { get; set; } = "";

        /// <summary>
        /// When enabled, subtitle start times are snapped forward to detected speech onsets
        /// so a subtitle no longer appears during the silence before its line is spoken.
        /// whisper.cpp emits gapless segments (next.start == prev.end); this re-introduces
        /// the natural gaps using FFmpeg silence detection. Local whisper-cli only.
        ///
        /// Note: this is the older energy-based fallback. When <see cref="EnableVad"/> is on
        /// (the default) this FFmpeg pass is skipped, because native VAD usually handles speech-onset
        /// gaps — unless <see cref="AlignSubtitlesToSpeechWithVad"/> opts back into running it on top.
        /// </summary>
        public bool AlignSubtitlesToSpeech { get; set; } = true;

        /// <summary>
        /// When enabled, also runs the <see cref="AlignSubtitlesToSpeech"/> forward-snap (FFmpeg
        /// silence detection) even when <see cref="EnableVad"/> is on. Native VAD improves
        /// transcription but does not always correct whisper's tendency to start a cue slightly
        /// before speech, so on some content lines still appear early. Enabling this layers the
        /// forward-snap on top of VAD — it only ever moves an early cue later, never earlier.
        /// Off by default (the energy-based detector can be unreliable on some material); requires
        /// <see cref="AlignSubtitlesToSpeech"/> to be on. Local whisper-cli only. (Issue #78.)
        /// </summary>
        public bool AlignSubtitlesToSpeechWithVad { get; set; } = false;

        /// <summary>
        /// When enabled, whisper-cli runs with native Silero Voice Activity Detection
        /// (<c>--vad</c>), which makes the emitted subtitles start at real speech onset instead
        /// of during the preceding silence (whisper.cpp otherwise chains segments gaplessly).
        /// Requires the Silero VAD model, which the plugin auto-downloads. Local whisper-cli only.
        /// </summary>
        public bool EnableVad { get; set; } = true;

        /// <summary>
        /// Filesystem path to the Silero VAD ggml model. Auto-set to the last downloaded model's path,
        /// so since issue #105 this reflects "the last model written to disk", not necessarily the
        /// active version — <see cref="VadModelVersion"/> drives selection and the resolver ignores an
        /// auto-written path inside the managed vad/ dir. It may still be pointed at a custom Silero VAD
        /// ggml file OUTSIDE that dir, which is treated as an explicit external override and takes
        /// precedence over <see cref="VadModelVersion"/>. When empty, the plugin uses its default vad/
        /// data directory and downloads the selected model on first use if missing.
        /// </summary>
        public string VadModelPath { get; set; } = "";

        /// <summary>
        /// Which Silero VAD model to use, by selection key (see <c>ModelCatalog.VadModels</c>):
        /// "v5.1.2" (default) or "v6.2.0". Empty or unknown falls back to the default (v5.1.2), so
        /// existing installs keep their current model and timing on upgrade. Selecting a different
        /// version downloads it on the next run. Local whisper-cli only. (Issue #105.)
        /// </summary>
        public string VadModelVersion { get; set; } = "";

        // ── Native VAD tuning (issue #105) ──
        // Each parameter maps to a whisper-cli --vad-* flag. The negative sentinel means "unset — let
        // whisper.cpp use its built-in default", so the default configuration emits NO --vad tuning
        // flags and produces the exact same command line as before this feature. Only applies when
        // EnableVad is on; a matching flag placed in CustomWhisperArgs supersedes these (appended last).

        /// <summary>VAD speech-probability threshold (whisper default 0.5). -1 = use whisper's default.</summary>
        public float VadThreshold { get; set; } = -1f;

        /// <summary>Minimum speech segment length in ms; shorter segments are dropped (whisper default 250). -1 = default.</summary>
        public int VadMinSpeechDurationMs { get; set; } = -1;

        /// <summary>Minimum silence in ms that splits two speech segments (whisper default 100). -1 = default.</summary>
        public int VadMinSilenceDurationMs { get; set; } = -1;

        /// <summary>Maximum speech segment length in seconds; longer ones auto-split (whisper default unlimited). Any value &lt;= 0 (default -1) leaves it unlimited — 0 is not a meaningful cap, so it is treated as unset like the negative sentinel.</summary>
        public float VadMaxSpeechDurationS { get; set; } = -1f;

        /// <summary>Padding in ms added to each side of a speech segment (whisper default 30). -1 = default.</summary>
        public int VadSpeechPadMs { get; set; } = -1;

        /// <summary>Audio overlap in seconds carried between consecutive segments (whisper default 0.1). -1 = default.</summary>
        public float VadSamplesOverlap { get; set; } = -1f;

        /// <summary>
        /// When enabled, compensates for a container audio start-time offset (the audio stream
        /// not starting at 0:00) by shifting all subtitle timestamps forward by that offset,
        /// keeping subtitles in sync with playback. Local whisper-cli only.
        /// </summary>
        public bool CompensateAudioOffset { get; set; } = true;

        /// <summary>
        /// When enabled (default), the auto-generation task skips media that already has a usable
        /// subtitle satisfying its need — for the translation pass, an existing English subtitle
        /// track (embedded or external) counts as already-translated. Prevents needlessly
        /// transcribing/translating media that is already subtitled. (Issue #82.)
        /// </summary>
        public bool SkipIfSubtitleExists { get; set; } = true;

        /// <summary>
        /// When enabled (default), FORCED subtitle tracks do NOT count as satisfying the
        /// subtitle need (a forced track only covers foreign-dialogue inserts, not full dialogue).
        /// Feeds the <see cref="SkipIfSubtitleExists"/> decision.
        /// </summary>
        public bool IgnoreForcedSubtitles { get; set; } = true;

        /// <summary>
        /// Whether the auto-generation task transcribes each title in its own spoken (audio)
        /// language — e.g. a Korean film gets Korean subtitles, an English film gets English.
        /// This is the primary "generate subtitles" switch. Default true. An English subtitle for
        /// non-English audio is a separate concern handled by <see cref="EnableTranslation"/>.
        /// (Issue #83.) A manual single-item "Generate" always transcribes regardless.
        /// </summary>
        public bool GenerateOriginalLanguageSubtitles { get; set; } = true;

        /// <summary>
        /// When enabled, image-based subtitle tracks (PGS/VOBSUB) count as an existing usable
        /// subtitle for the skip decision. Default false: image subs are not text and can't be
        /// searched/edited, so by default the plugin still generates a text subtitle. (Issue #83.)
        /// </summary>
        public bool CountImageSubtitlesAsPresent { get; set; } = false;

        /// <summary>
        /// Issue #110: cache the per-item "already has subtitles" verdict so repeat scheduled runs skip
        /// the filesystem re-check for unchanged items (a large library can otherwise take many minutes
        /// just to re-confirm existing subtitles every run). The cache keys on the item's change token
        /// (Jellyfin <c>DateLastSaved</c>) plus a signature of the skip-related settings, so any content
        /// or config change re-checks that item. Scheduled task only.
        /// </summary>
        public bool CacheSkippedItems { get; set; } = true;

        /// <summary>
        /// Backstop for <see cref="CacheSkippedItems"/>: re-verify a cached "complete" item after this
        /// many days even if its change token is unchanged, so a subtitle deleted OUTSIDE Jellyfin
        /// (which does not bump the token until a library scan) is eventually re-generated. 0 disables
        /// the time backstop (rely purely on the change token). Default 30.
        /// </summary>
        public int SkipCacheExpiryDays { get; set; } = 30;

        public List<string> EnabledLibraries { get; set; } = new List<string>();

        public PluginConfiguration()
        {
        }
    }
}
