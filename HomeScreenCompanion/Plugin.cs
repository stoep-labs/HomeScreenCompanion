using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Drawing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HomeScreenCompanion
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
    {
        public override string Name => "Home Screen Companion";

        public override Guid Id => new Guid("7c10708f-43e4-4d69-923c-77d01802315b");

        public override string Description => "Auto-tagging, collection management and home screen sync for Emby.";

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            AppPaths = applicationPaths;
            XmlSerializer = xmlSerializer;
        }

        public static Plugin Instance { get; private set; } = null!;
        public static IApplicationPaths AppPaths { get; private set; } = null!;
        public static new IXmlSerializer XmlSerializer { get; private set; } = null!;

        public override void UpdateConfiguration(BasePluginConfiguration configuration)
        {
            // First, before anything is queued or changed: a page of another version (one the
            // browser kept from before an update) does not know the newer fields and would save
            // them empty. Its save is refused; the page asks for a reload.
            if (configuration is PluginConfiguration incoming)
            {
                var pageVersion = incoming.PageVersion;
                incoming.PageVersion = "";
                if (!SameVersion(pageVersion, Version))
                {
                    PluginLog.Write("WRN", "Settings", $"Settings not saved: the settings page is out of date (page {(string.IsNullOrWhiteSpace(pageVersion) ? "of an older version" : "v" + pageVersion)}, plugin v{Version}) — reload the page");
                    throw new ArgumentException(StalePageMessage);
                }
            }
            // Sources with Top-List on: their TopLists entries follow the source (first, so the
            // steps below see the lists as saved).
            SourceTopLists.Derive(Configuration, configuration as PluginConfiguration);
            if (configuration is PluginConfiguration saved)
                PluginLog.Write("INF", "Settings", $"Settings saved — {RunLog.Plural(saved.Tags?.Count ?? 0, "source entry", "source entries")}, {RunLog.Plural(saved.TopLists?.Count ?? 0, "top-list")}");
            var wasMirroring = Configuration?.TopListMirrorCollections ?? false;
            HomeScreenCompanionTask.KeepLastRunCounts(Configuration, configuration as PluginConfiguration);
            // A changed row Position: the next run moves the rows already there (see RowPlacement).
            RowPlacement.NoteChanges(Configuration, configuration as PluginConfiguration);
            // Groups deleted on the settings page: their tag and home rows are cleaned up by the next full run.
            HomeScreenCompanionTask.RecordDeletedGroups(Configuration, configuration as PluginConfiguration);
            LegacyForkCleanup.KeepCleanupDone(Configuration, configuration as PluginConfiguration);
            base.UpdateConfiguration(configuration);
            HomeScreenCompanionTask.RecordImportedCollections(Configuration);
            // Toggling the collection mirror adds (or removes) memberships for all existing copies.
            if (Configuration!.TopListMirrorCollections != wasMirroring)
                TopListCollectionMirror.QueueFullSync();
        }

        internal const string StalePageMessage = "HSC_PAGE_OUT_OF_DATE: this settings page is out of date. Reload the page and save again.";

        // "4.4.2" and "4.4.2.0" are the same version.
        private static bool SameVersion(string? page, Version plugin)
        {
            if (!System.Version.TryParse((page ?? "").Trim(), out var p)) return false;
            int Part(int v) => v < 0 ? 0 : v;
            return Part(p.Major) == Part(plugin.Major) && Part(p.Minor) == Part(plugin.Minor)
                && Part(p.Build) == Part(plugin.Build) && Part(p.Revision) == Part(plugin.Revision);
        }

        // Every config save (the settings page and the server's own saves) can change which
        // titles carry a badge on their real poster (during a sync: rebuilt once it has finished).
        public override void SaveConfiguration()
        {
            base.SaveConfiguration();
            HomeScreenCompanionTask.RecordImportedCollections(Configuration);
            RealPosterBadges.ConfigSaved();
        }

        public IEnumerable<PluginPageInfo> GetPages()
        {
            var assembly = GetType().Assembly;

            var htmlPath = assembly.GetManifestResourceNames().FirstOrDefault(r => r.EndsWith("configPage.html"));
            var jsPath = assembly.GetManifestResourceNames().FirstOrDefault(r => r.EndsWith("configPage.js"));

            if (htmlPath == null || jsPath == null)
            {
                return new List<PluginPageInfo>();
            }

            return new[]
            {
                new PluginPageInfo
                {
                    Name = "HomeScreenCompanion",
                    EmbeddedResourcePath = htmlPath,

                    EnableInMainMenu = true,
                    DisplayName = "Home Screen Companion",
                    MenuIcon = "home"
                },
                new PluginPageInfo
                {
                    Name = "HomeScreenCompanionJS",
                    EmbeddedResourcePath = jsPath
                }
            };
        }

        public Stream GetThumbImage()
        {
            var type = GetType();
            var thumbPath = type.Assembly.GetManifestResourceNames().FirstOrDefault(r => r.EndsWith("thumb.png"));
            return (thumbPath != null ? type.Assembly.GetManifestResourceStream(thumbPath) : Stream.Null) ?? Stream.Null;
        }

        public ImageFormat ThumbImageFormat => ImageFormat.Png;
    }
}