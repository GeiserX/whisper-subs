using System.Text.Json.Serialization;
using MediaBrowser.Model.Plugins;

namespace WhisperSubs.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        public string WhisperModelPath { get; set; } = "";
        public string WhisperBinaryPath { get; set; } = "";
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
        /// (the default), whisper.cpp's native Silero VAD handles speech-onset gaps far more
        /// reliably and this FFmpeg pass is skipped.
        /// </summary>
        public bool AlignSubtitlesToSpeech { get; set; } = true;

        /// <summary>
        /// When enabled, whisper-cli runs with native Silero Voice Activity Detection
        /// (<c>--vad</c>), which makes the emitted subtitles start at real speech onset instead
        /// of during the preceding silence (whisper.cpp otherwise chains segments gaplessly).
        /// Requires the Silero VAD model, which the plugin auto-downloads. Local whisper-cli only.
        /// </summary>
        public bool EnableVad { get; set; } = true;

        /// <summary>
        /// Filesystem path to the Silero VAD ggml model used by <see cref="EnableVad"/>.
        /// Set automatically when the plugin downloads the VAD model; can be overridden to point
        /// at a custom Silero VAD ggml file. When empty, the plugin looks in its default
        /// vad/ data directory and downloads the model on first use if missing.
        /// </summary>
        public string VadModelPath { get; set; } = "";

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

        public List<string> EnabledLibraries { get; set; } = new List<string>();

        /// <summary>
        /// When enabled, fires an HTTP POST to the configured Lingarr URL after each subtitle
        /// is generated, so Lingarr can auto-translate the new subtitle. Off by default.
        /// </summary>
        public bool EnableLingarrNotification { get; set; } = false;

        /// <summary>
        /// Base URL of the Lingarr instance to notify after subtitle generation.
        /// Example: http://lingarr:8080
        /// </summary>
        public string LingarrUrl { get; set; } = "";

        /// <summary>
        /// API key sent as X-Api-Key header in the Lingarr webhook request.
        /// </summary>
        public string LingarrApiKey { get; set; } = "";

        public PluginConfiguration()
        {
        }
    }
}
