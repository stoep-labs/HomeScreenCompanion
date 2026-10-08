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
    /// Counts unique viewers per title for the "Popular on this server" rule: the number of
    /// different users who watched it (Emby marks it played) in the last N days, 0 = all time.
    /// A show counts once per viewer, however many episodes they watched. Emby keeps only each
    /// user's last play per item, so plays within a window cannot be counted, and titles marked
    /// watched without a date only count for all time. Each user's records are read once per
    /// run (one call per user) and matched to titles by user-data key.
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

        public Dictionary<Guid, int> CountViewers(List<BaseItem> titles, int days, Action<string> log)
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

            var cutoff = days > 0 ? DateTimeOffset.UtcNow.AddDays(-days) : (DateTimeOffset?)null;
            var viewers = new Dictionary<Guid, HashSet<long>>();
            int users = 0, hits = 0;

            foreach (var user in _userManager.GetUserList(new UserQuery { IsDisabled = false }))
            {
                users++;
                foreach (var record in RecordsOf(user.InternalId))
                {
                    if (record.Key == null || !target.TryGetValue(record.Key, out var titleId)) continue;
                    hits++;
                    if (!Counts(record, cutoff)) continue;
                    if (!viewers.TryGetValue(titleId, out var set)) viewers[titleId] = set = new HashSet<long>();
                    set.Add(user.InternalId);
                }
            }

            log($"Popularity: {titles.Count} title(s), {target.Count} watch key(s), {users} user(s), {hits} viewing record(s) matched, {viewers.Count} title(s) watched "
                + (cutoff.HasValue ? $"in the last {days} day(s)" : "ever"));
            return viewers.ToDictionary(kv => kv.Key, kv => kv.Value.Count);
        }

        // Also used by NextWatchRecommender, so "popular" means the same in both.
        internal static bool Counts(UserItemData d, DateTimeOffset? cutoff)
        {
            bool watched = d.Played || d.PlayCount > 0;
            if (!watched) return false;
            if (!cutoff.HasValue) return true;
            return d.LastPlayedDate.HasValue && d.LastPlayedDate.Value >= cutoff.Value;
        }

        // One GetAllUserData call per user per run, shared with NextWatchRecommender.
        internal List<UserItemData> RecordsOf(long userInternalId)
        {
            if (!_recordsByUser.TryGetValue(userInternalId, out var records))
                _recordsByUser[userInternalId] = records = _userDataManager.GetAllUserData(userInternalId) ?? new List<UserItemData>();
            return records;
        }

        internal static string KeyOf(BaseItem item)
        {
            var key = item.UserDataKey;
            if (string.IsNullOrEmpty(key))
            {
                try { key = item.GetUserDataKey(null); } catch { }
            }
            return key ?? "";
        }
    }

    /// <summary>Result of HomeScreenCompanionTask.PreviewEntryAsync.</summary>
    internal sealed class SourcePreview
    {
        public SourcePreview(TagConfig source, List<TagConfig>? group = null)
        {
            Source = source;
            Group = group != null && group.Count > 0 ? group : new List<TagConfig> { source };
        }

        public TagConfig Source { get; }
        // Every URL / collection / playlist of the source card (one TagConfig each, as saved).
        public List<TagConfig> Group { get; }
        // Titles on the list that are not in the library, in list order (External / AI).
        public List<PreviewMissingTitle> Missing { get; } = new List<PreviewMissingTitle>();
        public List<string> Warnings { get; private set; } = new List<string>();
        public string Note { get; set; } = "";
        public bool Done { get; private set; }
        public string Message { get; set; } = "";
        public int Scanned { get; private set; }
        public List<BaseItem> Items { get; private set; } = new List<BaseItem>();
        public Dictionary<Guid, int> Viewers { get; private set; } = new Dictionary<Guid, int>();
        // "Your Next Watch": the user the preview is for, and a short reason per title.
        public string UserId { get; set; } = "";
        public Dictionary<Guid, string> Reasons { get; } = new Dictionary<Guid, string>();

        public void Complete(List<BaseItem> items, int scanned, Dictionary<Guid, int>? viewers, List<string>? warnings = null)
        {
            Items = items.ToList();
            Scanned = scanned;
            Viewers = viewers ?? new Dictionary<Guid, int>();
            Warnings = warnings?.ToList() ?? new List<string>();
            Done = true;
        }
    }

    internal sealed class PreviewMissingTitle
    {
        public PreviewMissingTitle(string title, int? year, string imdb) { Title = title ?? ""; Year = year; Imdb = imdb ?? ""; }
        public string Title { get; }
        public int? Year { get; }
        public string Imdb { get; }
    }
}
