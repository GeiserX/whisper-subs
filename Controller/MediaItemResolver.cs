using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace WhisperSubs.Controller
{
    /// <summary>
    /// Shared resolution of a requested item into the supported leaf media items under it (#112). Used
    /// by both the admin Generate-all path and the user-request path so a container (Series/Season) fans
    /// out to exactly the same set — the fan-out that the user quota / caps count.
    /// </summary>
    // Excluded from coverage: a thin resolver over the ILibraryManager runtime + Jellyfin item types
    // (ResolveLeafItems queries the live library); the only pure bit is trivial enum-name parsing.
    [ExcludeFromCodeCoverage(Justification = "Thin resolver over ILibraryManager + Jellyfin runtime item types")]
    public static class MediaItemResolver
    {
        /// <summary>
        /// The only item kinds an admin GenerateAll / user request may target: media leaves
        /// (Video and its subclasses Movie/Episode/MusicVideo/Trailer; Audio) and the intended
        /// containers (Series, Season, MusicAlbum). Everything else — library roots, BoxSet
        /// collections, MusicArtist, plain Folders — is rejected to prevent an uncapped recursive
        /// fan-out over the whole library. This allow-list subsumes the old whole-library-container
        /// reject-list (a CollectionFolder / UserView / AggregateFolder is none of these types).
        /// </summary>
        public static bool IsAllowedGenerateTarget(BaseItem item)
            => item is Video
            || item is MediaBrowser.Controller.Entities.Audio.Audio
            || item is MediaBrowser.Controller.Entities.TV.Series
            || item is MediaBrowser.Controller.Entities.TV.Season
            || item is MediaBrowser.Controller.Entities.Audio.MusicAlbum;

        /// <summary>
        /// Resolves the supported leaf media items (Video, or Audio when lyrics are enabled) under
        /// <paramref name="parent"/>. If <paramref name="parent"/> is itself a supported leaf, returns
        /// just it. Items without a filesystem path are excluded.
        /// </summary>
        public static List<BaseItem> ResolveLeafItems(BaseItem parent, bool enableLyrics, ILibraryManager libraryManager)
        {
            var typeStr = enableLyrics ? "Movie,Episode,Video,Audio" : "Movie,Episode,Video";
            var includeTypes = GetBaseItemKinds(typeStr);

            var children = libraryManager.GetItemList(new InternalItemsQuery
            {
                ParentId = parent.Id,
                IncludeItemTypes = includeTypes,
                Recursive = true
            }).Where(item => !string.IsNullOrEmpty(item.Path)).ToList();

            // If this IS a leaf item itself, include it directly.
            if (children.Count == 0
                && !string.IsNullOrEmpty(parent.Path)
                && (parent is Video || (parent is MediaBrowser.Controller.Entities.Audio.Audio && enableLyrics)))
            {
                children = new List<BaseItem> { parent };
            }

            return children;
        }

        /// <summary>Parses a comma-separated Jellyfin item-type list into <see cref="BaseItemKind"/> values.</summary>
        public static BaseItemKind[] GetBaseItemKinds(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return Array.Empty<BaseItemKind>();
            }

            var parts = input.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            var converter = TypeDescriptor.GetConverter(typeof(BaseItemKind));
            var result = new List<BaseItemKind>();

            foreach (var part in parts)
            {
                var trimmed = part.Trim();
                if (converter.IsValid(trimmed) && Enum.TryParse<BaseItemKind>(trimmed, true, out var kind))
                {
                    result.Add(kind);
                }
            }

            return result.ToArray();
        }
    }
}
