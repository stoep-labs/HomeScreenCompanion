using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HomeScreenCompanion
{
    // Sonarr / Radarr on the Missing tab: whether a missing movie is in Radarr and a missing show in
    // Sonarr (monitored, downloaded), and Add for the ones that are not. The plugin calls Sonarr /
    // Radarr itself (X-Api-Key), so the keys never reach the browser and https / CORS do not matter.

    [Route("/HomeScreenCompanion/Arr/Test", "POST")]
    [Authenticated(Roles = "Admin")]
    public class ArrTestRequest : IReturn<ArrTestResponse>
    {
        public string Kind { get; set; } = "";   // "sonarr" | "radarr"
        public string Url { get; set; } = "";
        public string ApiKey { get; set; } = "";  // "" = the saved key (only for the saved URL)
    }

    public class ArrTestResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public string Version { get; set; } = "";
    }

    [Route("/HomeScreenCompanion/Arr/Status", "POST")]
    [Authenticated(Roles = "Admin")]
    public class ArrStatusRequest : IReturn<ArrStatusResponse>
    {
        public List<ArrStatusItem> Items { get; set; } = new List<ArrStatusItem>();
    }

    public class ArrStatusItem
    {
        public string Type { get; set; } = "";   // "movie" (Radarr) | "show" (Sonarr), as the Missing tab lists it
        public string Imdb { get; set; } = "";
        public string Tmdb { get; set; } = "";
        public string Tvdb { get; set; } = "";
        // The answer:
        public bool In { get; set; }
        public bool Monitored { get; set; }
        public bool Downloaded { get; set; }      // Radarr: the movie has its file
        public int EpisodeFiles { get; set; }     // Sonarr: episodes it has a file of …
        public int Episodes { get; set; }         // … of its monitored, aired episodes
    }

    public class ArrStatusResponse
    {
        public bool SonarrConfigured { get; set; }
        public bool RadarrConfigured { get; set; }
        // Set when the server could not be read (its titles then come without a status).
        public string SonarrError { get; set; } = "";
        public string RadarrError { get; set; } = "";
        public List<ArrStatusItem> Items { get; set; } = new List<ArrStatusItem>();
    }

    [Route("/HomeScreenCompanion/Arr/Options", "GET")]
    [Authenticated(Roles = "Admin")]
    public class ArrOptionsRequest : IReturn<ArrOptionsResponse>
    {
        public string Kind { get; set; } = "";
    }

    public class ArrOptionsResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public List<string> RootFolders { get; set; } = new List<string>();
        public List<ArrProfile> QualityProfiles { get; set; } = new List<ArrProfile>();
        public string LastRootFolderPath { get; set; } = "";
    }

    public class ArrProfile
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
    }

    [Route("/HomeScreenCompanion/Arr/Add", "POST")]
    [Authenticated(Roles = "Admin")]
    public class ArrAddRequest : IReturn<ArrAddResponse>
    {
        public string Kind { get; set; } = "";
        public string SourceId { get; set; } = "";   // IMDb id; or a TVDb id (Sonarr) / TMDb id (Radarr)
        public string Title { get; set; } = "";
        public int QualityProfileId { get; set; }
        public string RootFolderPath { get; set; } = "";
        public string MinimumAvailability { get; set; } = "announced";
        public bool Monitored { get; set; } = true;
    }

    public class ArrAddResponse
    {
        public bool Success { get; set; }
        public bool AlreadyIn { get; set; }
        public string Message { get; set; } = "";
    }

    public class ArrService : IService
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        private const int MaxConcurrentSonarrLookups = 3;
        private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(2);

        // The full Sonarr / Radarr library lists, kept briefly: the tab asks in parts, and again
        // after an Add, and each list can hold thousands of entries.
        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, (DateTime At, List<ArrLibraryEntry> List)> LibraryCache = new Dictionary<string, (DateTime, List<ArrLibraryEntry>)>();
        // A show's TVDb id by its IMDb id, as Sonarr's lookup answered it (null = not found).
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int?> ImdbTvdbCache = new System.Collections.Concurrent.ConcurrentDictionary<string, int?>();

        private readonly IJsonSerializer _json;

        public ArrService(IJsonSerializer jsonSerializer)
        {
            _json = jsonSerializer;
        }

        private static PluginConfiguration Config => Plugin.Instance!.Configuration;

        private static string NormUrl(string? url) => (url ?? "").Trim().TrimEnd('/');

        private static (string Url, string Key) Settings(string kind)
        {
            var c = Config;
            return kind == "sonarr"
                ? (NormUrl(c.SonarrUrl), (c.SonarrApiKey ?? "").Trim())
                : (NormUrl(c.RadarrUrl), (c.RadarrApiKey ?? "").Trim());
        }

        internal static bool Configured(PluginConfiguration c, string kind)
        {
            var url = kind == "sonarr" ? c.SonarrUrl : c.RadarrUrl;
            var key = kind == "sonarr" ? c.SonarrApiKey : c.RadarrApiKey;
            return !string.IsNullOrWhiteSpace(url) && !string.IsNullOrWhiteSpace(key);
        }

        private static string Kind(string? kind) => string.Equals((kind ?? "").Trim(), "sonarr", StringComparison.OrdinalIgnoreCase) ? "sonarr" : "radarr";
        private static string Name(string kind) => kind == "sonarr" ? "Sonarr" : "Radarr";

        private static async Task<(int Status, string Body)> Send(HttpMethod method, string baseUrl, string apiKey, string path, string? jsonBody = null)
        {
            var req = new HttpRequestMessage(method, baseUrl + path);
            req.Headers.Add("X-Api-Key", apiKey);
            if (jsonBody != null) req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            using (var res = await Http.SendAsync(req).ConfigureAwait(false))
            {
                var body = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                return ((int)res.StatusCode, body);
            }
        }

        private async Task<T?> GetJson<T>(string baseUrl, string apiKey, string path) where T : class
        {
            var (status, body) = await Send(HttpMethod.Get, baseUrl, apiKey, path).ConfigureAwait(false);
            if (status == 401) throw new InvalidOperationException("HTTP 401 (wrong API key)");
            if (status < 200 || status > 299) throw new InvalidOperationException("HTTP " + status);
            return _json.DeserializeFromString<T>(body);
        }

        // ---- Test connection ----

        public async Task<object> Post(ArrTestRequest request)
        {
            var kind = Kind(request.Kind);
            var url = NormUrl(request.Url);
            // The saved key goes only to the saved URL: a key field left empty for another address is not filled in.
            var (savedUrl, savedKey) = Settings(kind);
            var key = !string.IsNullOrWhiteSpace(request.ApiKey) ? request.ApiKey.Trim()
                : string.Equals(url, savedUrl, StringComparison.OrdinalIgnoreCase) ? savedKey : "";
            if (url.Length == 0 || key.Length == 0)
                return new ArrTestResponse { Success = false, Message = "Connection failed: URL and API key are required." };
            try
            {
                var status = await GetJson<ArrSystemStatus>(url, key, "/api/v3/system/status").ConfigureAwait(false);
                var version = status?.version ?? "";
                return new ArrTestResponse { Success = true, Version = version, Message = $"Connected to {Name(kind)} {(version.Length > 0 ? version : "")}".TrimEnd() };
            }
            catch (Exception ex)
            {
                return new ArrTestResponse { Success = false, Message = "Connection failed: " + ex.Message };
            }
        }

        // ---- Library lists ----

        private async Task<List<ArrLibraryEntry>> LibraryList(string kind, string baseUrl, string apiKey)
        {
            var cacheKey = kind + "|" + baseUrl + "|" + apiKey;
            lock (CacheLock)
            {
                if (LibraryCache.TryGetValue(cacheKey, out var hit) && DateTime.UtcNow - hit.At < CacheFor) return hit.List;
            }
            var list = await GetJson<List<ArrLibraryEntry>>(baseUrl, apiKey, kind == "sonarr" ? "/api/v3/series" : "/api/v3/movie").ConfigureAwait(false)
                ?? new List<ArrLibraryEntry>();
            lock (CacheLock) LibraryCache[cacheKey] = (DateTime.UtcNow, list);
            return list;
        }

        private static void ForgetLibrary(string kind)
        {
            lock (CacheLock)
            {
                foreach (var k in LibraryCache.Keys.Where(k => k.StartsWith(kind + "|", StringComparison.Ordinal)).ToList())
                    LibraryCache.Remove(k);
            }
        }

        private async Task<int?> SonarrTvdbId(string imdb, string baseUrl, string apiKey)
        {
            var cacheKey = baseUrl + "|" + imdb;
            if (ImdbTvdbCache.TryGetValue(cacheKey, out var cached)) return cached;
            int? tvdb = null;
            try
            {
                var results = await GetJson<List<ArrLibraryEntry>>(baseUrl, apiKey, "/api/v3/series/lookup?term=imdb:" + Uri.EscapeDataString(imdb)).ConfigureAwait(false);
                var first = results?.FirstOrDefault();
                if (first != null && first.tvdbId > 0) tvdb = first.tvdbId;
            }
            catch (Exception swallowed) { PluginLog.Swallowed(swallowed); return null; }   // not cached: asked again next time
            ImdbTvdbCache[cacheKey] = tvdb;
            return tvdb;
        }

        // ---- Status of the Missing tab's titles ----

        public async Task<object> Post(ArrStatusRequest request)
        {
            var (sUrl, sKey) = Settings("sonarr");
            var (rUrl, rKey) = Settings("radarr");
            var response = new ArrStatusResponse
            {
                SonarrConfigured = sUrl.Length > 0 && sKey.Length > 0,
                RadarrConfigured = rUrl.Length > 0 && rKey.Length > 0,
                Items = request.Items ?? new List<ArrStatusItem>()
            };
            var items = response.Items;
            foreach (var it in items)
            {
                it.Type = (it.Type ?? "").Trim().ToLowerInvariant() == "show" ? "show" : "movie";
                it.Imdb = (it.Imdb ?? "").Trim();
                it.Tmdb = (it.Tmdb ?? "").Trim();
                it.Tvdb = (it.Tvdb ?? "").Trim();
            }

            List<ArrLibraryEntry>? series = null, movies = null;
            var seriesTask = response.SonarrConfigured && items.Any(i => i.Type == "show") ? LibraryList("sonarr", sUrl, sKey) : null;
            var moviesTask = response.RadarrConfigured && items.Any(i => i.Type == "movie") ? LibraryList("radarr", rUrl, rKey) : null;
            if (seriesTask != null)
            {
                try { series = await seriesTask.ConfigureAwait(false); }
                catch (Exception ex) { response.SonarrError = ex.Message; }
            }
            if (moviesTask != null)
            {
                try { movies = await moviesTask.ConfigureAwait(false); }
                catch (Exception ex) { response.RadarrError = ex.Message; }
            }

            // Radarr: by IMDb id, else by TMDb id (only for a title without an IMDb id: the tab's
            // TMDb id of a title of unknown type can be a show's).
            if (movies != null)
            {
                var byImdb = movies.Where(m => !string.IsNullOrEmpty(m.imdbId)).GroupBy(m => m.imdbId!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                var byTmdb = movies.Where(m => m.tmdbId > 0).GroupBy(m => m.tmdbId).ToDictionary(g => g.Key, g => g.First());
                foreach (var it in items.Where(i => i.Type == "movie"))
                {
                    ArrLibraryEntry? m = null;
                    if (it.Imdb.Length > 0) byImdb.TryGetValue(it.Imdb, out m);
                    else if (int.TryParse(it.Tmdb, out var tmdb)) byTmdb.TryGetValue(tmdb, out m);
                    if (m == null) continue;
                    it.In = true;
                    it.Monitored = m.monitored;
                    it.Downloaded = m.hasFile;
                }
            }

            // Sonarr: by TVDb id, else IMDb id, else Sonarr's lookup of the IMDb id (a few at a time).
            if (series != null)
            {
                var byTvdb = series.Where(s => s.tvdbId > 0).GroupBy(s => s.tvdbId).ToDictionary(g => g.Key, g => g.First());
                var byImdb = series.Where(s => !string.IsNullOrEmpty(s.imdbId)).GroupBy(s => s.imdbId!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                void Fill(ArrStatusItem it, ArrLibraryEntry s)
                {
                    it.In = true;
                    it.Monitored = s.monitored;
                    it.EpisodeFiles = s.statistics?.episodeFileCount ?? 0;
                    it.Episodes = s.statistics?.episodeCount ?? 0;
                }
                var toLookUp = new List<ArrStatusItem>();
                foreach (var it in items.Where(i => i.Type == "show"))
                {
                    if (int.TryParse(it.Tvdb, out var tvdb) && byTvdb.TryGetValue(tvdb, out var s)) Fill(it, s);
                    else if (it.Imdb.Length > 0 && byImdb.TryGetValue(it.Imdb, out s)) Fill(it, s);
                    else if (it.Tvdb.Length == 0 && it.Imdb.StartsWith("tt", StringComparison.OrdinalIgnoreCase)) toLookUp.Add(it);
                }
                if (toLookUp.Count > 0)
                {
                    var gate = new SemaphoreSlim(MaxConcurrentSonarrLookups);
                    await Task.WhenAll(toLookUp.Select(async it =>
                    {
                        await gate.WaitAsync().ConfigureAwait(false);
                        try
                        {
                            var tvdb = await SonarrTvdbId(it.Imdb, sUrl, sKey).ConfigureAwait(false);
                            if (tvdb.HasValue && byTvdb.TryGetValue(tvdb.Value, out var s)) Fill(it, s);
                        }
                        finally { gate.Release(); }
                    })).ConfigureAwait(false);
                }
            }
            return response;
        }

        // ---- Add dialog: root folders and quality profiles ----

        public async Task<object> Get(ArrOptionsRequest request)
        {
            var kind = Kind(request.Kind);
            var (url, key) = Settings(kind);
            if (url.Length == 0 || key.Length == 0)
                return new ArrOptionsResponse { Success = false, Message = $"{Name(kind)} URL or API key not set." };
            try
            {
                var foldersTask = GetJson<List<ArrRootFolder>>(url, key, "/api/v3/rootfolder");
                var profilesTask = GetJson<List<ArrQualityProfile>>(url, key, "/api/v3/qualityprofile");
                var folders = await foldersTask.ConfigureAwait(false) ?? new List<ArrRootFolder>();
                var profiles = await profilesTask.ConfigureAwait(false) ?? new List<ArrQualityProfile>();
                return new ArrOptionsResponse
                {
                    Success = true,
                    RootFolders = folders.Select(f => f.path ?? "").Where(p => p.Length > 0).ToList(),
                    QualityProfiles = profiles.Select(p => new ArrProfile { Id = p.id, Name = p.name ?? "" }).ToList(),
                    LastRootFolderPath = (kind == "sonarr" ? Config.SonarrLastRootFolderPath : Config.RadarrLastRootFolderPath) ?? ""
                };
            }
            catch (Exception ex)
            {
                return new ArrOptionsResponse { Success = false, Message = $"{Name(kind)} could not be read: {ex.Message}" };
            }
        }

        // ---- Add ----

        public async Task<object> Post(ArrAddRequest request)
        {
            var kind = Kind(request.Kind);
            var (url, key) = Settings(kind);
            if (url.Length == 0 || key.Length == 0)
                return new ArrAddResponse { Success = false, Message = $"{Name(kind)} URL or API key not set." };
            var sourceId = (request.SourceId ?? "").Trim();
            try
            {
                var result = kind == "sonarr"
                    ? await AddToSonarr(url, key, sourceId, request).ConfigureAwait(false)
                    : await AddToRadarr(url, key, sourceId, request).ConfigureAwait(false);
                if (result.Success || result.AlreadyIn) ForgetLibrary(kind);
                if (result.Success)
                {
                    PluginLog.Write("INF", "Missing", $"{Name(kind)}: added '{request.Title}' ({sourceId})");
                    // The root folder is picked again next time. Not a settings change of the page
                    // (left out of its "changed elsewhere" check), so no badge rebuild either.
                    var root = (request.RootFolderPath ?? "").Trim();
                    if (root.Length > 0 && root != ((kind == "sonarr" ? Config.SonarrLastRootFolderPath : Config.RadarrLastRootFolderPath) ?? ""))
                    {
                        if (kind == "sonarr") Config.SonarrLastRootFolderPath = root;
                        else Config.RadarrLastRootFolderPath = root;
                        Plugin.Instance!.SaveUsersAndRows();
                    }
                }
                return result;
            }
            catch (Exception ex)
            {
                PluginLog.Write("WRN", "Missing", $"{Name(kind)}: '{request.Title}' could not be added: {ex.Message}");
                return new ArrAddResponse { Success = false, Message = $"Not added: {ex.Message}" };
            }
        }

        private string Body(Dictionary<string, object> fields) => _json.SerializeToString(fields);

        private async Task<ArrAddResponse> AddToSonarr(string url, string key, string sourceId, ArrAddRequest request)
        {
            const string already = "Already in Sonarr.";
            // A TVDb id or an IMDb id: Sonarr's lookup gives the show to add.
            bool isImdb = sourceId.StartsWith("tt", StringComparison.OrdinalIgnoreCase);
            if (!isImdb && !(sourceId.Length > 0 && sourceId.All(char.IsDigit)))
                return new ArrAddResponse { Message = $"Not added: no IMDb or TVDb id." };
            var existing = await LibraryList("sonarr", url, key).ConfigureAwait(false);
            if (isImdb && existing.Any(s => string.Equals(s.imdbId, sourceId, StringComparison.OrdinalIgnoreCase)))
                return new ArrAddResponse { AlreadyIn = true, Message = already };

            var lookup = await GetJson<List<ArrLibraryEntry>>(url, key, "/api/v3/series/lookup?term=" + (isImdb ? "imdb:" : "tvdb:") + Uri.EscapeDataString(sourceId)).ConfigureAwait(false);
            var series = lookup?.FirstOrDefault(s => s.tvdbId > 0);
            if (series == null)
                return new ArrAddResponse { Message = $"Not added: Sonarr does not find {(isImdb ? "IMDb" : "TVDb")} id {sourceId}." };
            if (existing.Any(s => s.tvdbId == series.tvdbId))
                return new ArrAddResponse { AlreadyIn = true, Message = already };

            var body = Body(new Dictionary<string, object>
            {
                ["tvdbId"] = series.tvdbId,
                ["title"] = series.title ?? request.Title ?? "",
                ["qualityProfileId"] = request.QualityProfileId,
                ["rootFolderPath"] = request.RootFolderPath ?? "",
                ["monitored"] = true,
                ["seasonFolder"] = true,
                ["addOptions"] = new Dictionary<string, object> { ["searchForMissingEpisodes"] = true }
            });
            var (status, resBody) = await Send(HttpMethod.Post, url, key, "/api/v3/series", body).ConfigureAwait(false);
            if (status >= 200 && status <= 299)
                return new ArrAddResponse { Success = true, Message = $"Added to Sonarr: {series.title ?? request.Title}." };
            return ArrError(resBody, status, "SeriesExistsValidator", already);
        }

        private async Task<ArrAddResponse> AddToRadarr(string url, string key, string sourceId, ArrAddRequest request)
        {
            const string already = "Already in Radarr.";
            bool isImdb = sourceId.StartsWith("tt", StringComparison.OrdinalIgnoreCase);
            int.TryParse(sourceId, out var sourceTmdb);
            if (!isImdb && sourceTmdb <= 0)
                return new ArrAddResponse { Message = $"Not added: no IMDb or TMDb id." };
            var existing = await LibraryList("radarr", url, key).ConfigureAwait(false);
            if (existing.Any(m => isImdb ? string.Equals(m.imdbId, sourceId, StringComparison.OrdinalIgnoreCase) : m.tmdbId == sourceTmdb))
                return new ArrAddResponse { AlreadyIn = true, Message = already };

            int tmdbId = sourceTmdb;
            if (isImdb)
            {
                var found = await GetJson<List<ArrLibraryEntry>>(url, key, "/api/v3/movie/lookup?term=imdb:" + Uri.EscapeDataString(sourceId)).ConfigureAwait(false);
                tmdbId = found?.FirstOrDefault(m => m.tmdbId > 0)?.tmdbId ?? 0;
                if (tmdbId <= 0)
                    return new ArrAddResponse { Message = $"Not added: Radarr does not find IMDb id {sourceId}." };
                if (existing.Any(m => m.tmdbId == tmdbId))
                    return new ArrAddResponse { AlreadyIn = true, Message = already };
            }

            var body = Body(new Dictionary<string, object>
            {
                ["title"] = request.Title ?? "",
                ["tmdbId"] = tmdbId,
                ["qualityProfileId"] = request.QualityProfileId,
                ["rootFolderPath"] = request.RootFolderPath ?? "",
                ["monitored"] = request.Monitored,
                ["minimumAvailability"] = string.IsNullOrWhiteSpace(request.MinimumAvailability) ? "announced" : request.MinimumAvailability,
                ["addOptions"] = new Dictionary<string, object> { ["searchForMovie"] = true }
            });
            var (status, resBody) = await Send(HttpMethod.Post, url, key, "/api/v3/movie", body).ConfigureAwait(false);
            if (status >= 200 && status <= 299)
                return new ArrAddResponse { Success = true, Message = $"Added to Radarr: {request.Title}." };
            return ArrError(resBody, status, "MovieExistsValidator", already);
        }

        // Sonarr / Radarr answer a refused add with a list of validation errors.
        private ArrAddResponse ArrError(string body, int status, string existsCode, string already)
        {
            try
            {
                var first = _json.DeserializeFromString<List<ArrValidationError>>(body)?.FirstOrDefault();
                if (first != null)
                {
                    if (first.errorCode == existsCode) return new ArrAddResponse { AlreadyIn = true, Message = already };
                    if (!string.IsNullOrEmpty(first.errorMessage)) return new ArrAddResponse { Message = "Not added: " + first.errorMessage };
                }
            }
            catch (Exception swallowed) { PluginLog.Swallowed(swallowed); }
            return new ArrAddResponse { Message = $"Not added: HTTP {status}" };
        }

        // ---- JSON shapes ----

        public class ArrSystemStatus { public string? version { get; set; } }
        public class ArrLibraryEntry
        {
            public int tvdbId { get; set; }
            public int tmdbId { get; set; }
            public string? imdbId { get; set; }
            public string? title { get; set; }
            public bool monitored { get; set; }
            public bool hasFile { get; set; }                 // Radarr
            public ArrSeriesStatistics? statistics { get; set; }   // Sonarr
        }
        public class ArrSeriesStatistics
        {
            public int episodeFileCount { get; set; }
            public int episodeCount { get; set; }
        }
        public class ArrRootFolder { public string? path { get; set; } }
        public class ArrQualityProfile { public int id { get; set; } public string? name { get; set; } }
        public class ArrValidationError
        {
            public string? errorMessage { get; set; }
            public string? errorCode { get; set; }
        }
    }
}
