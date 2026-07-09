using System.Text.Json.Serialization;

namespace WhisperSubs.Configuration
{
    /// <summary>
    /// Chooses which audio-track languages get transcribed when <see cref="PluginConfiguration.DefaultLanguage"/>
    /// is "auto" and a file has MORE THAN ONE audio language. With "auto" the plugin detects every audio
    /// track's language and, by default, transcribes each one (producing a <c>.&lt;lang&gt;.generated.srt</c>
    /// per audio language). This toggle lets an admin restrict that to only the primary/default audio track.
    /// Serialized by NAME over the config REST API (<see cref="JsonStringEnumConverter"/>); the config page
    /// uses the string value.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum AudioLanguageSelection
    {
        /// <summary>
        /// Transcribe EVERY detected audio-track language (default, existing behavior): one
        /// <c>.&lt;lang&gt;.generated.srt</c> per audio language present.
        /// </summary>
        All = 0,

        /// <summary>
        /// Transcribe ONLY the primary (first-listed) audio track's language; secondary audio languages
        /// are skipped. Only affects the "auto" multi-language case — a specific
        /// <see cref="PluginConfiguration.DefaultLanguage"/> already resolves to a single language, and the
        /// no-tags whisper-auto-detect fallback is already a single pass.
        /// </summary>
        PrimaryOnly = 1,
    }
}
