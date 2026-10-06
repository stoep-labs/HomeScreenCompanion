using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Users;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace HomeScreenCompanion
{
    /// <summary>
    /// Keeps movie top-list libraries out of sight (setting "Hide top-list libraries", on by
    /// default): for every user they are excluded from "My Media" tiles and from the "Latest"
    /// rows, both in Emby's default home screen (user configuration) and in saved home rows
    /// (each row's own excluded folders). Emby's sidebar always lists every library a user can
    /// access, so a top-list library still appears there for users who can see it.
    ///
    /// Runs on every top-list section sync, so new users and new top-lists are covered.
    /// Turning the setting off removes the exclusions again.
    /// </summary>
    internal static class TopListLibraryVisibility
    {
        public static int Apply(PluginConfiguration config, IUserManager userManager, Action<string>? log = null)
        {
            var libIds = (config.TopLists ?? new List<TopListHomeSection>())
                .Where(t => !ShowTopList.IsShowList(t)
                         && !string.IsNullOrEmpty(t.HomeSectionLibraryId) && t.HomeSectionLibraryId != "auto")
                .Select(t => t.HomeSectionLibraryId.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (libIds.Count == 0) return 0;

            bool hide = config.HideTopListLibraries;
            int changedUsers = 0;

            foreach (var user in userManager.GetUserList(new UserQuery()))
            {
                try
                {
                    bool changed = false;

                    var cfg = userManager.GetUserConfiguration(user);
                    var myMedia = Merge(cfg.MyMediaExcludes, libIds, hide);
                    var latest = Merge(cfg.LatestItemsExcludes, libIds, hide);
                    if (myMedia != null || latest != null)
                    {
                        if (myMedia != null) cfg.MyMediaExcludes = myMedia;
                        if (latest != null) cfg.LatestItemsExcludes = latest;
                        userManager.UpdateConfiguration(user, cfg);
                        changed = true;
                    }

                    // Saved home rows ignore the user configuration and carry their own exclusions.
                    var sections = userManager.GetHomeSections(user.InternalId, CancellationToken.None)?.Sections
                                   ?? Array.Empty<ContentSection>();
                    foreach (var section in sections)
                    {
                        if (section.SectionType != "latestmediablock" && section.SectionType != "userviews") continue;
                        var excluded = Merge(section.ExcludedFolders, libIds, hide);
                        if (excluded == null) continue;
                        section.ExcludedFolders = excluded;
                        userManager.UpdateHomeSection(user.InternalId, section, CancellationToken.None);
                        changed = true;
                    }

                    if (changed) changedUsers++;
                }
                catch (Exception ex)
                {
                    log?.Invoke($"Hiding top-list libraries for '{user.Name}' failed — {ex.Message}");
                }
            }
            return changedUsers;
        }

        // The new list, or null when nothing changes.
        private static string[]? Merge(string[]? current, List<string> libIds, bool hide)
        {
            var list = (current ?? Array.Empty<string>()).ToList();
            bool changed = false;
            foreach (var id in libIds)
            {
                bool present = list.Contains(id, StringComparer.OrdinalIgnoreCase);
                if (hide && !present) { list.Add(id); changed = true; }
                if (!hide && present) { list.RemoveAll(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase)); changed = true; }
            }
            return changed ? list.ToArray() : null;
        }
    }
}
