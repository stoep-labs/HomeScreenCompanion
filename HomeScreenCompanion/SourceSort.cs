using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HomeScreenCompanion
{
    /// <summary>
    /// "Pick the top N by" for Local Media Information (Smart Playlist) sources. Without it the
    /// source keeps the first N matches as before; with it, all matches are ranked and the top N
    /// kept. This only decides which titles get the tag — a home row orders them by its own
    /// Sort By. The ranking is also the source's rank order (tag_ranks), so top-lists built from
    /// the tag follow it.
    ///
    /// Popularity = unique viewers: the number of users who watched the title within the last
    /// N days (0 = all time). A show counts once per viewer however many episodes they watched.
    /// Emby keeps only each user's last play per item, so "plays in a window" cannot be counted;
    /// items marked watched without a date only count in "all time".
    /// </summary>
    internal static class SourceSort
    {
        public static bool IsSorted(TagConfig t) => !string.IsNullOrWhiteSpace(t.MiSortBy);

        public static List<BaseItem> Sort(List<BaseItem> items, TagConfig t, PopularityCounter popularity, Action<string> log)
        {
            bool desc = !string.Equals(t.MiSortOrder, "Ascending", StringComparison.OrdinalIgnoreCase);
            switch ((t.MiSortBy ?? "").Trim())
            {
                case "Popularity":
                {
                    var viewers = popularity.CountViewers(items, t, log);
                    int min = Math.Max(1, t.PopularityMinViewers);
                    var kept = items.Where(i => viewers.TryGetValue(i.Id, out var v) && v >= min);
                    // Always most-watched first: the point is to pick the popular titles.
                    return kept.OrderByDescending(i => viewers[i.Id])
                                  .ThenByDescending(i => i.CommunityRating ?? 0)
                                  .ThenBy(i => i.SortName, StringComparer.OrdinalIgnoreCase)
                                  .ToList();
                }
                case "DateAdded":
                    return Order(items, i => i.DateCreated, desc);
                case "PremiereDate":
                    return Order(items, i => i.PremiereDate ?? (i.ProductionYear.HasValue ? new DateTimeOffset(i.ProductionYear.Value, 1, 1, 0, 0, 0, TimeSpan.Zero) : DateTimeOffset.MinValue), desc);
                case "CommunityRating":
                    return Order(items, i => i.CommunityRating ?? 0, desc);
                case "Name":
                    return desc
                        ? items.OrderByDescending(i => i.SortName, StringComparer.OrdinalIgnoreCase).ToList()
                        : items.OrderBy(i => i.SortName, StringComparer.OrdinalIgnoreCase).ToList();
                case "Random":
                    var rng = new Random();
                    return items.OrderBy(_ => rng.Next()).ToList();
                default:
                    return items;
            }
        }

        private static List<BaseItem> Order<TKey>(List<BaseItem> items, Func<BaseItem, TKey> key, bool desc)
            => (desc ? items.OrderByDescending(key) : items.OrderBy(key))
                .ThenBy(i => i.SortName, StringComparer.OrdinalIgnoreCase)
                .ToList();
    }

    /// <summary>
    /// Counts unique viewers per title from Emby's user data. Each user's records are read once
    /// per sync run (one call per user) and matched to titles by user-data key.
    /// </summary>
    internal sealed class PopularityCounter
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly IUserDataManager _userDataManager;
        private readonly Dictionary<long, List<UserItemData>> _recordsByUser = new Dictionary<long, List<UserItemData>>();

        public PopularityCounter(ILibraryManager libraryManager, IUserManager userManager, IUserDataManager userDataManager)
        {
            _libraryManager = libraryManager;
            _userManager = userManager;
            _userDataManager = userDataManager;
        }

        public Dictionary<Guid, int> CountViewers(List<BaseItem> titles, TagConfig t, Action<string> log)
        {
            // user-data key → the title it counts for. Shows are counted through their episodes.
            var target = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            void Map(BaseItem item, Guid titleId)
            {
                var key = KeyOf(item);
                if (!string.IsNullOrEmpty(key) && !target.ContainsKey(key)) target[key] = titleId;
            }

            var seriesByInternalId = new Dictionary<long, Guid>();
            foreach (var title in titles)
            {
                if (title is Series) seriesByInternalId[title.InternalId] = title.Id;
                else Map(title, title.Id);
            }
            if (seriesByInternalId.Count > 0)
            {
                var episodes = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Episode" },
                    SeriesIds = seriesByInternalId.Keys.ToArray(),
                    Recursive = true,
                    IsVirtualItem = false
                });
                foreach (var ep in episodes.OfType<Episode>())
                    if (seriesByInternalId.TryGetValue(ep.SeriesId, out var seriesId)) Map(ep, seriesId);
            }

            var excluded = new HashSet<string>((t.PopularityExcludeUserIds ?? new List<string>()).Select(Norm), StringComparer.OrdinalIgnoreCase);
            var cutoff = t.PopularityDays > 0 ? DateTimeOffset.UtcNow.AddDays(-t.PopularityDays) : (DateTimeOffset?)null;
            var viewers = new Dictionary<Guid, HashSet<long>>();
            int users = 0, hits = 0;

            foreach (var user in _userManager.GetUserList(new UserQuery { IsDisabled = false }))
            {
                if (excluded.Contains(Norm(user.Id.ToString("N")))) continue;
                users++;
                foreach (var record in RecordsOf(user.InternalId))
                {
                    if (record.Key == null || !target.TryGetValue(record.Key, out var titleId)) continue;
                    hits++;
                    if (!Counts(record, cutoff, t.PopularityCountPartial)) continue;
                    if (!viewers.TryGetValue(titleId, out var set)) viewers[titleId] = set = new HashSet<long>();
                    set.Add(user.InternalId);
                }
            }

            log($"Popularity: {titles.Count} title(s), {target.Count} watch key(s), {users} user(s), {hits} viewing record(s) matched, {viewers.Count} title(s) watched "
                + (cutoff.HasValue ? $"in the last {t.PopularityDays} day(s)" : "ever"));
            return viewers.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
        }

        private static bool Counts(UserItemData d, DateTimeOffset? cutoff, bool countPartial)
        {
            bool watched = d.Played || d.PlayCount > 0 || (countPartial && d.PlaybackPositionTicks > 0);
            if (!watched) return false;
            if (!cutoff.HasValue) return true;
            return d.LastPlayedDate.HasValue && d.LastPlayedDate.Value >= cutoff.Value;
        }

        private List<UserItemData> RecordsOf(long userInternalId)
        {
            if (!_recordsByUser.TryGetValue(userInternalId, out var records))
                _recordsByUser[userInternalId] = records = _userDataManager.GetAllUserData(userInternalId) ?? new List<UserItemData>();
            return records;
        }

        private static string KeyOf(BaseItem item)
        {
            var key = item.UserDataKey;
            if (string.IsNullOrEmpty(key))
            {
                try { key = item.GetUserDataKey(null); } catch { }
            }
            return key ?? "";
        }

        private static string Norm(string id) => (id ?? "").Replace("-", "");
    }
}
