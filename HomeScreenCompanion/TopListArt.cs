using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HomeScreenCompanion
{
    // Art for the page a top-list row header opens (TopListPosterStyle / TopListBackgroundStyle).
    //
    // Emby builds the header link from the section's ParentId: a library opens the library page,
    // which never shows a background, and no ParentId opens a plain list page without one. A Tag
    // item opens the tag's own page, which shows the tag's own Backdrop. For an "items" section
    // Emby does not use a Tag ParentId to pick the row's items (the library exclusions do that),
    // so pointing ParentId at the list's source tag changes only where the header goes.
    //
    // So with art set, the header opens the source tag's page (movies: the tag named like the
    // list; shows: ShowSourceTag) and that tag item carries the art. With no art set, nothing
    // changes. Manual lists have no source tag, so they have no such page.
    internal static class TopListArt
    {
        internal const string ArtFolder = "toplist_art";

        internal static bool Wanted(TopListHomeSection tl) =>
            tl != null && (CollectionArtRenderer.IsStyle(tl.TopListPosterStyle) || CollectionArtRenderer.IsStyle(tl.TopListBackgroundStyle)
                || !string.IsNullOrWhiteSpace(tl.TopListBackgroundPath));

        // The tag whose page the header opens: movies — the tag the list is built from (it has
        // the list's name); shows — the tag the list is fed by. A manual list has no such tag
        // (TagItem finds none, so nothing changes).
        internal static string SourceTag(TopListHomeSection tl, PluginConfiguration? config)
        {
            if (tl == null) return "";
            return ShowTopList.IsShowList(tl) ? (tl.ShowSourceTag ?? "").Trim() : (tl.TagName ?? "").Trim();
        }

        internal static BaseItem? TagItem(ILibraryManager libraryManager, string tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            return libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = new[] { "Tag" }, Name = tag.Trim() })
                .FirstOrDefault(i => string.Equals(i.Name, tag.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        // ParentId for the list's home section: the source tag when art is set and the tag
        // exists, otherwise null (keep the default: the top-list library, or none for shows).
        internal static string? HeaderTargetId(TopListHomeSection tl, ILibraryManager libraryManager)
        {
            if (!Wanted(tl)) return null;
            var tag = TagItem(libraryManager, SourceTag(tl, Plugin.Instance?.Configuration));
            return tag == null ? null : tag.InternalId.ToString();
        }

        // True when a top-list with art owns this tag's page, so the Tags tab art leaves it alone.
        internal static bool OwnsTag(string tagName)
        {
            var config = Plugin.Instance?.Configuration;
            if (config == null || string.IsNullOrWhiteSpace(tagName)) return false;
            return (config.TopLists ?? new List<TopListHomeSection>())
                .Any(t => Wanted(t) && string.Equals(SourceTag(t, config), tagName.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        internal static bool IsTopListArt(string? path)
        {
            if (string.IsNullOrEmpty(path) || Plugin.Instance == null) return false;
            return path!.StartsWith(Path.Combine(Plugin.Instance.DataFolderPath, ArtFolder), StringComparison.OrdinalIgnoreCase);
        }

        // The list's titles in rank order, as the sync builds it (for Preview art): movies — the
        // originals carrying the tag, ordered by the tag's rank file, each film once, up to Max
        // items; shows — the ranked series of the source tag.
        internal static List<BaseItem> RankedTitles(TopListHomeSection tl, ILibraryManager libraryManager, IJsonSerializer jsonSerializer)
        {
            var tag = SourceTag(tl, Plugin.Instance?.Configuration);
            if (tag.Length == 0) return new List<BaseItem>();
            if (ShowTopList.IsShowList(tl))
            {
                var series = ShowTopList.RankedSeriesFromTag(libraryManager, jsonSerializer, tag);
                return series.Take(tl.MaxItems > 0 ? Math.Min(tl.MaxItems, ShowTopList.MaxRanks) : ShowTopList.MaxRanks).ToList();
            }

            var dataPath = Plugin.Instance!.DataFolderPath;
            var topListsFolder = Path.Combine(dataPath, "toplists") + Path.DirectorySeparatorChar;
            var items = libraryManager.GetItemList(new InternalItemsQuery
            {
                Tags = new[] { tag },
                IncludeItemTypes = new[] { "Movie" },
                Recursive = true,
                IsVirtualItem = false
            }).Where(i => string.IsNullOrEmpty(i.Path) || !i.Path.StartsWith(topListsFolder, StringComparison.OrdinalIgnoreCase)).ToList();

            var invalid = Path.GetInvalidFileNameChars();
            var safe = new string(tag.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray()).Trim('.');
            var rankFile = Path.Combine(dataPath, "tag_ranks", safe + ".json");
            if (File.Exists(rankFile))
            {
                try
                {
                    var rankIds = jsonSerializer.DeserializeFromFile<List<string>>(rankFile) ?? new List<string>();
                    var rankMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < rankIds.Count; i++)
                        if (!string.IsNullOrEmpty(rankIds[i]) && !rankMap.ContainsKey(rankIds[i])) rankMap[rankIds[i]] = i;
                    items = items.OrderBy(it =>
                    {
                        var imdb = it.GetProviderId("Imdb");
                        return !string.IsNullOrEmpty(imdb) && rankMap.TryGetValue(imdb, out var r) ? r : int.MaxValue;
                    }).ToList();
                }
                catch { }
            }

            var ranked = CollectionArtRenderer.DistinctTitles(items);
            int max = tl.MaxItems;
            if (max <= 0)
            {
                try
                {
                    var settings = jsonSerializer.DeserializeFromString<Dictionary<string, string>>(tl.HomeSectionSettings ?? "{}");
                    if (settings != null && settings.TryGetValue("MaxItems", out var m) && int.TryParse(m, out var parsed)) max = parsed;
                }
                catch { }
            }
            return max > 0 ? ranked.Take(max).ToList() : ranked;
        }
    }
}
