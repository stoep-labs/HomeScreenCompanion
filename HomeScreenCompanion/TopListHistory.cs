using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Serialization;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace HomeScreenCompanion
{
    /// <summary>
    /// What a top list looked like before, for the badge extras: the ranks of the last two
    /// different rankings (for the movement chip) and the ISO weeks each title has been in the
    /// list without dropping out (for "N wks"). One small JSON file per list in
    /// &lt;plugin data&gt;/toplist_history. Updated every time the list's art is drawn; a title that
    /// leaves the list starts again at week 1 when it comes back. Until a list has been ranked
    /// twice there is no movement to show, so none is drawn.
    /// </summary>
    internal static class TopListHistory
    {
        public sealed class HistoryFile
        {
            public Dictionary<string, int> Current { get; set; } = new Dictionary<string, int>();
            public Dictionary<string, int>? Previous { get; set; }
            public Dictionary<string, List<string>> Weeks { get; set; } = new Dictionary<string, List<string>>();
        }

        private static readonly object Gate = new object();

        public static Dictionary<Guid, RankExtras> Update(string listName, IList<BaseItem> ranked, string look,
            IJsonSerializer json, ILibraryManager libraryManager, IUserManager userManager, IUserDataManager userDataManager, Action<string>? log = null)
        {
            var result = new Dictionary<Guid, RankExtras>();
            try
            {
                var dir = Path.Combine(Plugin.Instance!.DataFolderPath, "toplist_history");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, string.Concat((listName ?? "list").Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)) + ".json");
                var ids = ranked.Select(i => i.Id.ToString("N")).ToList();
                var now = new Dictionary<string, int>();
                for (int i = 0; i < ids.Count; i++) if (!now.ContainsKey(ids[i])) now[ids[i]] = i + 1;

                HistoryFile? h = null;
                lock (Gate)
                {
                    if (File.Exists(path)) try { h = json.DeserializeFromFile<HistoryFile>(path); } catch { h = null; }
                    if (h == null) h = new HistoryFile { Current = now };
                    else if (!Same(h.Current, now)) { h.Previous = h.Current; h.Current = now; }

                    var week = IsoWeek(DateTime.UtcNow);
                    var weeks = new Dictionary<string, List<string>>();
                    foreach (var id in now.Keys)
                    {
                        var list = h.Weeks != null && h.Weeks.TryGetValue(id, out var w) && w != null ? w : new List<string>();
                        if (!list.Contains(week)) list.Add(week);
                        weeks[id] = list;
                    }
                    h.Weeks = weeks;
                    json.SerializeToFile(h, path);
                }

                Dictionary<Guid, int>? plays = null;
                if (BadgeLook.WantsPlays(look))
                    plays = new PopularityCounter(libraryManager, userManager, userDataManager).CountPlays(ranked.ToList());

                foreach (var item in ranked)
                {
                    var id = item.Id.ToString("N");
                    if (!now.TryGetValue(id, out var rank) || result.ContainsKey(item.Id)) continue;
                    var ex = new RankExtras { Weeks = h.Weeks.TryGetValue(id, out var wl) ? wl.Count : 1 };
                    if (h.Previous != null)
                        ex.Move = !h.Previous.TryGetValue(id, out var before) ? "NEW" : before == rank ? "=" : before > rank ? "+" + (before - rank) : "-" + (rank - before);
                    if (plays != null) ex.Plays = plays.TryGetValue(item.Id, out var p) ? p : 0;
                    result[item.Id] = ex;
                }
            }
            catch (Exception ex)
            {
                log?.Invoke($"Top-list '{listName}': rank history could not be updated — {ex.Message}");
            }
            return result;
        }

        private static bool Same(Dictionary<string, int>? a, Dictionary<string, int> b)
            => a != null && a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

        private static string IsoWeek(DateTime d)
        {
            var cal = CultureInfo.InvariantCulture.Calendar;
            var day = cal.GetDayOfWeek(d);
            if (day >= DayOfWeek.Monday && day <= DayOfWeek.Wednesday) d = d.AddDays(3);
            int w = cal.GetWeekOfYear(d, CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);
            return d.Year.ToString(CultureInfo.InvariantCulture) + "-W" + w.ToString("00", CultureInfo.InvariantCulture);
        }
    }
}
