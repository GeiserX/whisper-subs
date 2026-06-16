using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace WhisperSubs.Controller
{
    /// <summary>
    /// Adapts a Jellyfin <see cref="BaseItem"/>'s media streams into the provider-agnostic
    /// <see cref="SubtitleStreamInfo"/> list consumed by <see cref="SubtitleInventory"/>.
    /// Reads BOTH embedded and external subtitle streams via <c>item.GetMediaStreams()</c>
    /// (populated by Jellyfin's library scan — see issue #82 research).
    /// </summary>
    [ExcludeFromCodeCoverage(Justification = "Thin adapter over Jellyfin's MediaStream API")]
    public static class SubtitleStreamReader
    {
        public static IReadOnlyList<SubtitleStreamInfo> GetSubtitleStreams(BaseItem item)
        {
            try
            {
                return item.GetMediaStreams()
                    .Where(s => s.Type == MediaStreamType.Subtitle)
                    .Select(s => new SubtitleStreamInfo
                    {
                        Language = s.Language,
                        IsExternal = s.IsExternal,
                        IsForced = s.IsForced,
                        IsTextSubtitle = s.IsTextSubtitleStream,
                        Path = s.Path
                    })
                    .ToList();
            }
            catch
            {
                // Never let a metadata read break the task; treat as "unknown / no streams".
                return new List<SubtitleStreamInfo>();
            }
        }
    }
}
