using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TabloExtractinator4000.Models;

namespace TabloExtractinator4000.Services;

// ---------------------------------------------------------------------------
// All local device API calls — fetching recordings, server info, storage,
// and (crucially) the watch/stream URL used to feed ffmpeg.
//
// Every request is HMAC-MD5 signed via TabloAuthService.MakeDeviceAuthHeader.
// ---------------------------------------------------------------------------
public class TabloApiService
{
    private readonly HttpClient       _http;
    private readonly TabloAuthService _auth;

    // Populated after each GetAllRecordingsAsync call — paths that failed to parse.
    public IReadOnlyList<string> ParseErrors { get; private set; } = [];

    // Written alongside the exe when any recording fails to parse.
    private static readonly string LogPath = Path.Combine(
        AppContext.BaseDirectory, "parse_errors.log");

    public TabloApiService(HttpClient http, TabloAuthService auth)
    {
        _http = http;
        _auth = auth;
    }

    // ---------------------------------------------------------------------------
    // Server info
    // ---------------------------------------------------------------------------
    public async Task<DeviceInfo> GetServerInfoAsync(CancellationToken ct = default)
    {
        var node = await LocalGetAsync("/server/info", ct);
        return new DeviceInfo(
            ServerId:     node["server_id"]?.GetValue<string>() ?? "",
            Name:         node["name"]?.GetValue<string>()      ?? "Tablo",
            Version:      node["version"]?.GetValue<string>()   ?? "",
            ModelName:    node["model"]?["name"]?.GetValue<string>() ?? "",
            Tuners:       node["model"]?["tuners"]?.GetValue<int>() ?? 0,
            LocalAddress: node["local_address"]?.GetValue<string>() ?? ""
        );
    }

    // ---------------------------------------------------------------------------
    // Storage
    // ---------------------------------------------------------------------------
    public async Task<List<StorageInfo>> GetStorageAsync(CancellationToken ct = default)
    {
        var node = await LocalGetAsync("/server/harddrives", ct);
        var result = new List<StorageInfo>();
        if (node is not JsonArray arr) return result;
        foreach (var item in arr)
        {
            result.Add(new StorageInfo(
                Name:       item!["name"]?.GetValue<string>() ?? "",
                TotalBytes: item["size"]?.GetValue<long>()    ?? 0,
                UsedBytes:  item["usage"]?.GetValue<long>()   ?? 0,
                FreeBytes:  item["free"]?.GetValue<long>()    ?? 0,
                Connected:  item["connected"]?.GetValue<bool>() ?? false
            ));
        }
        return result;
    }

    // ---------------------------------------------------------------------------
    // All recordings — returns a flat list of IRecording covering episodes + movies
    // ---------------------------------------------------------------------------
    public async Task<List<IRecording>> GetAllRecordingsAsync(
        IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var results  = new List<IRecording>();
        var skipped  = new List<string>();   // paths that failed to parse

        // Episodes (series recordings)
        progress?.Report("Loading series list…");
        var seriesPaths = await GetIdArrayAsync("/recordings/series", ct);
        foreach (var seriesPath in seriesPaths)
        {
            ct.ThrowIfCancellationRequested();
            var episodePaths = await GetIdArrayAsync($"{seriesPath}/episodes", ct);
            foreach (var epPath in episodePaths)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var ep = await GetEpisodeAsync(epPath, ct);
                    results.Add(ep);
                }
                catch (Exception ex) { skipped.Add($"{epPath}: {ex.Message}"); await LogParseErrorAsync(epPath, ex, ct); }
            }
        }

        // Movies
        progress?.Report("Loading movies…");
        var movieSummaryPaths = await GetIdArrayAsync("/recordings/movies", ct);
        foreach (var summaryPath in movieSummaryPaths)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var summaryNode = await LocalGetAsync(summaryPath, ct);
                var airingPath  = summaryNode["user_info"]?["up_next"]?.GetValue<string>();
                if (airingPath == null) continue;
                var movie = await GetMovieAsync(airingPath, summaryNode, ct);
                results.Add(movie);
            }
            catch (Exception ex) { skipped.Add($"{summaryPath}: {ex.Message}"); await LogParseErrorAsync(summaryPath, ex, ct); }
        }

        // Sports — /recordings/sports may return either:
        //   • Direct event nodes (have video_details) — parse as-is
        //   • Series container nodes (no video_details, have user_info.up_next and/or /events sub-array)
        progress?.Report("Loading sports…");
        var sportPaths = await GetIdArrayAsync("/recordings/sports", ct);
        foreach (var sportPath in sportPaths)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var node = await LocalGetAsync(sportPath, ct);
                if (node["video_details"] != null)
                {
                    // Direct event recording — parse it directly
                    results.Add(await GetSportAsync(sportPath, ct));
                }
                else
                {
                    // Series container — collect event paths from up_next and /events sub-array
                    var eventPaths = new List<string>();
                    var upNext = node["user_info"]?["up_next"]?.GetValue<string>();
                    if (upNext != null) eventPaths.Add(upNext);
                    try
                    {
                        var subEvents = await GetIdArrayAsync($"{sportPath}/events", ct);
                        foreach (var ep in subEvents)
                            if (!eventPaths.Contains(ep)) eventPaths.Add(ep);
                    }
                    catch { }

                    foreach (var eventPath in eventPaths)
                    {
                        ct.ThrowIfCancellationRequested();
                        try { results.Add(await GetSportAsync(eventPath, ct)); }
                        catch (Exception ex) { skipped.Add($"{eventPath}: {ex.Message}"); await LogParseErrorAsync(eventPath, ex, ct); }
                    }
                }
            }
            catch (Exception ex) { skipped.Add($"{sportPath}: {ex.Message}"); await LogParseErrorAsync(sportPath, ex, ct); }
        }

        // Discover any additional recording categories (e.g. FAST/streaming channels).
        // GET /recordings returns an array of sub-paths like ["/recordings/series", ...].
        // We try any path we haven't already fetched.
        var knownCategories = new HashSet<string> { "/recordings/series", "/recordings/movies", "/recordings/sports" };
        var rootPaths = await GetIdArrayAsync("/recordings", ct);
        foreach (var catPath in rootPaths.Where(p => !knownCategories.Contains(p)))
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Loading {catPath}…");
            var itemPaths = await GetIdArrayAsync(catPath, ct);
            // Try each item: first as episode (has airing_details+episode), then as standalone
            foreach (var itemPath in itemPaths)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // Check if this is a container (has sub-items like series/episodes)
                    var subPaths = await GetIdArrayAsync($"{itemPath}/episodes", ct);
                    if (subPaths.Count > 0)
                    {
                        foreach (var epPath in subPaths)
                        {
                            try { results.Add(await GetEpisodeAsync(epPath, ct)); }
                            catch (Exception ex) { skipped.Add($"{epPath}: {ex.Message}"); }
                        }
                        continue;
                    }
                    // Standalone recording
                    results.Add(await GetStandaloneProgramAsync(itemPath, ct));
                }
                catch (Exception ex) { skipped.Add($"{itemPath}: {ex.Message}"); }
            }
        }

        var msg = $"Loaded {results.Count} recordings.";
        if (skipped.Count > 0)
            msg += $" ({skipped.Count} skipped — check ParseErrors property for details)";
        ParseErrors = skipped;
        progress?.Report(msg);
        return results;
    }

    private async Task LogParseErrorAsync(string path, Exception ex, CancellationToken ct)
    {
        try
        {
            string rawJson = "(could not fetch)";
            try
            {
                var node = await LocalGetAsync(path, ct);
                rawJson = JsonSerializer.Serialize(node,
                    new JsonSerializerOptions { WriteIndented = true });
            }
            catch { }

            var entry = $"""
                ========== {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} ==========
                PATH:  {path}
                ERROR: {ex}
                JSON:
                {rawJson}

                """;
            await File.AppendAllTextAsync(LogPath, entry, ct);
        }
        catch { }
    }

    // ---------------------------------------------------------------------------
    // Stream (playlist) URL — POST to the recording's /watch endpoint.
    // Returns the HLS playlist_url that ffmpeg will consume.
    //
    // The watch endpoint expects a POST body identical to the channel watch body
    // used by tablo2plex. Tablo responds with:
    //   { "playlist_url": "http://...", "token": "...", "expires": "...", ... }
    // ---------------------------------------------------------------------------
    // Returns (playlistUrl, keepaliveIntervalSeconds).
    // keepaliveIntervalSeconds is from the watch response — caller should re-POST
    // to /watch every (keepaliveIntervalSeconds - 45)s to keep the session alive.
    public async Task<(string PlaylistUrl, int KeepaliveSeconds)> GetPlaylistUrlAsync(
        IRecording recording, CancellationToken ct = default)
    {
        var node = await PostWatchAsync(recording.Path, ct);

        var playlistUrl = node["playlist_url"]?.GetValue<string>()
            ?? throw new InvalidOperationException(
                $"No playlist_url in watch response for {recording.Path}. " +
                $"Full response: {node.ToJsonString()}");

        var keepalive = node["keepalive"]?.GetValue<int>() ?? 165;
        return (playlistUrl, keepalive);
    }

    // Re-POSTs to /watch to reset the session expiry timer.
    // Call this every (keepaliveSeconds - 45)s while ffmpeg is downloading.
    public async Task SendKeepaliveAsync(IRecording recording, CancellationToken ct = default)
    {
        try { await PostWatchAsync(recording.Path, ct); }
        catch { /* keepalive failures are non-fatal — ffmpeg may still succeed */ }
    }

    private async Task<System.Text.Json.Nodes.JsonNode> PostWatchAsync(string recordingPath, CancellationToken ct)
    {
        var watchPath = recordingPath.TrimEnd('/') + "/watch";

        var bodyObj = new
        {
            bandwidth = (object?)null,
            extra = new
            {
                limitedAdTracking = 1,
                deviceOSVersion   = "16.6",
                lang              = "en_US",
                height            = 1080,
                deviceId          = "00000000-0000-0000-0000-000000000000",
                width             = 1920,
                deviceModel       = "iPhone10,1",
                deviceMake        = "Apple",
                deviceOS          = "iOS"
            },
            device_id = Guid.NewGuid().ToString(),
            platform  = "ios"
        };
        var bodyJson = System.Text.Json.JsonSerializer.Serialize(bodyObj);
        return await LocalPostAsync(watchPath, bodyJson, ct);
    }

    // ---------------------------------------------------------------------------
    // Delete a recording from the Tablo.
    // Only called by the delete pipeline AFTER ffmpeg + ffprobe verification pass.
    // ---------------------------------------------------------------------------
    public async Task DeleteRecordingAsync(IRecording recording, CancellationToken ct = default)
    {
        // DELETE /recordings/series/episodes/{id}  or  /recordings/movies/airings/{id}
        await LocalDeleteAsync(recording.Path, ct);
    }

    // ---------------------------------------------------------------------------
    // Private parsing helpers
    // ---------------------------------------------------------------------------
    private async Task<EpisodeRecording> GetEpisodeAsync(string path, CancellationToken ct)
    {
        var n  = await LocalGetAsync(path, ct);
        var ad = n["airing_details"] ?? throw new InvalidOperationException("Missing airing_details");
        var vd = n["video_details"]  ?? throw new InvalidOperationException("Missing video_details");
        var ep = n["episode"];

        // OTA channels nest channel info as channel.channel; FAST/streaming channels may not.
        var chOuter = ad["channel"];
        var ch      = chOuter?["channel"] ?? chOuter;
        var network = ch?["network"]?.GetValue<string>()
                   ?? ch?["call_sign"]?.GetValue<string>()
                   ?? chOuter?["name"]?.GetValue<string>()
                   ?? "";

        var flags = vd["flags"]?.AsArray().Select(f => f?.GetValue<string>() ?? "").ToHashSet() ?? [];
        return new EpisodeRecording(
            ObjectId:        n["object_id"]!.GetValue<int>(),
            Path:            n["path"]!.GetValue<string>(),
            SeriesPath:      n["series_path"]?.GetValue<string>() ?? "",
            SeriesTitle:     ad["show_title"]?.GetValue<string>() ?? "",
            EpisodeTitle:    ep?["title"]?.GetValue<string>()     ?? "",
            Description:     ep?["description"]?.GetValue<string>(),
            SeasonNumber:    ep?["season_number"]?.GetValue<int?>(),
            EpisodeNumber:   ep?["number"]?.GetValue<int?>(),
            OriginalAirDate: TryParseDate(ep?["orig_air_date"]?.GetValue<string>()),
            AiredAt:         DateTimeOffset.Parse(ad["datetime"]!.GetValue<string>()),
            DurationSeconds: vd["duration"]?.GetValue<int>()  ?? 0,
            ScheduledSeconds:ad["duration"]?.GetValue<int>()  ?? 0,
            SizeBytes:       vd["size"]?.GetValue<long>()     ?? 0,
            State:           vd["state"]?.GetValue<string>()  ?? "",
            NetworkName:     network,
            ChannelMajor:    ch?["major"]?.GetValue<int>()    ?? 0,
            ChannelMinor:    ch?["minor"]?.GetValue<int>()    ?? 0,
            ContainerFormat: vd["container_format"]?.GetValue<string>() ?? "mpeg2",
            Width:           vd["width"]?.GetValue<int>()     ?? 0,
            Height:          vd["height"]?.GetValue<int>()    ?? 0,
            AudioFormat:     vd["audio"]?.GetValue<string>()  ?? "",
            IsInterlaced:    flags.Contains("interlaced"),
            Watched:         n["user_info"]?["watched"]?.GetValue<bool>() ?? false
        );
    }

    private async Task<MovieRecording> GetMovieAsync(string airingPath, JsonNode summaryNode, CancellationToken ct)
    {
        var n  = await LocalGetAsync(airingPath, ct);
        var ad = n["airing_details"] ?? throw new InvalidOperationException("Missing airing_details");
        var vd = n["video_details"]  ?? throw new InvalidOperationException("Missing video_details");
        var mv = summaryNode["movie"];

        var chOuter = ad["channel"];
        var ch      = chOuter?["channel"] ?? chOuter;
        var network = ch?["network"]?.GetValue<string>()
                   ?? ch?["call_sign"]?.GetValue<string>()
                   ?? chOuter?["name"]?.GetValue<string>()
                   ?? "";

        var flags = vd["flags"]?.AsArray().Select(f => f?.GetValue<string>() ?? "").ToHashSet() ?? [];
        return new MovieRecording(
            ObjectId:        n["object_id"]!.GetValue<int>(),
            Path:            n["path"]!.GetValue<string>(),
            MoviePath:       n["movie_path"]?.GetValue<string>() ?? "",
            Title:           mv?["title"]?.GetValue<string>() ?? ad["show_title"]?.GetValue<string>() ?? "",
            ReleaseYear:     mv?["release_year"]?.GetValue<int?>(),
            FilmRating:      mv?["film_rating"]?.GetValue<string>(),
            AiredAt:         DateTimeOffset.Parse(ad["datetime"]!.GetValue<string>()),
            DurationSeconds: vd["duration"]?.GetValue<int>()  ?? 0,
            ScheduledSeconds:ad["duration"]?.GetValue<int>()  ?? 0,
            SizeBytes:       vd["size"]?.GetValue<long>()     ?? 0,
            State:           vd["state"]?.GetValue<string>()  ?? "",
            NetworkName:     network,
            ChannelMajor:    ch?["major"]?.GetValue<int>()    ?? 0,
            ChannelMinor:    ch?["minor"]?.GetValue<int>()    ?? 0,
            ContainerFormat: vd["container_format"]?.GetValue<string>() ?? "mpeg2",
            Width:           vd["width"]?.GetValue<int>()     ?? 0,
            Height:          vd["height"]?.GetValue<int>()    ?? 0,
            AudioFormat:     vd["audio"]?.GetValue<string>()  ?? "",
            IsInterlaced:    flags.Contains("interlaced"),
            Watched:         n["user_info"]?["watched"]?.GetValue<bool>() ?? false
        );
    }

    private async Task<SportRecording> GetSportAsync(string path, CancellationToken ct)
    {
        var n  = await LocalGetAsync(path, ct);
        var ad = n["airing_details"];   // may be null for broken/failed recordings
        var vd = n["video_details"] ?? throw new InvalidOperationException("Missing video_details");

        var chOuter = ad?["channel"];
        var ch      = chOuter?["channel"] ?? chOuter;
        var network = ch?["network"]?.GetValue<string>()
                   ?? ch?["call_sign"]?.GetValue<string>()
                   ?? chOuter?["name"]?.GetValue<string>()
                   ?? "";

        var flags = vd["flags"]?.AsArray().Select(f => f?.GetValue<string>() ?? "").ToHashSet() ?? [];

        var datetimeStr = ad?["datetime"]?.GetValue<string>();
        var airedAt = datetimeStr != null
            ? DateTimeOffset.Parse(datetimeStr)
            : DateTimeOffset.MinValue;

        return new SportRecording(
            ObjectId:        n["object_id"]!.GetValue<int>(),
            Path:            n["path"]!.GetValue<string>(),
            SportPath:       n["sport_path"]?.GetValue<string>() ?? path,
            Title:           ad?["show_title"]?.GetValue<string>() ?? "(unknown sport)",
            LeagueTitle:     n["sport"]?["league"]?.GetValue<string>(),
            AiredAt:         airedAt,
            DurationSeconds: vd["duration"]?.GetValue<int>()   ?? 0,
            ScheduledSeconds:ad?["duration"]?.GetValue<int>()  ?? 0,
            SizeBytes:       vd["size"]?.GetValue<long>()      ?? 0,
            State:           vd["state"]?.GetValue<string>()   ?? "",
            NetworkName:     network,
            ChannelMajor:    ch?["major"]?.GetValue<int>()     ?? 0,
            ChannelMinor:    ch?["minor"]?.GetValue<int>()     ?? 0,
            ContainerFormat: vd["container_format"]?.GetValue<string>() ?? "mpeg2",
            Width:           vd["width"]?.GetValue<int>()      ?? 0,
            Height:          vd["height"]?.GetValue<int>()     ?? 0,
            AudioFormat:     vd["audio"]?.GetValue<string>()   ?? "",
            IsInterlaced:    flags.Contains("interlaced"),
            Watched:         n["user_info"]?["watched"]?.GetValue<bool>() ?? false
        );
    }

    // Parses a standalone program recording (FAST channel / manual).
    // These aren't part of a series; treated as a single-episode series grouped by show title.
    private async Task<EpisodeRecording> GetStandaloneProgramAsync(string path, CancellationToken ct)
    {
        var n  = await LocalGetAsync(path, ct);
        var ad = n["airing_details"] ?? throw new InvalidOperationException("Missing airing_details");
        var vd = n["video_details"]  ?? throw new InvalidOperationException("Missing video_details");

        var chOuter = ad["channel"];
        var ch      = chOuter?["channel"] ?? chOuter;
        string network = ch?["network"]?.GetValue<string>()
                      ?? ch?["call_sign"]?.GetValue<string>()
                      ?? chOuter?["name"]?.GetValue<string>()
                      ?? "";
        int major = ch?["major"]?.GetValue<int>() ?? 0;
        int minor = ch?["minor"]?.GetValue<int>() ?? 0;

        var showTitle = ad["show_title"]?.GetValue<string>() ?? "Unknown";
        var epNode    = n["episode"];
        var epTitle   = epNode?["title"]?.GetValue<string>() ?? "";
        var season    = epNode?["season_number"]?.GetValue<int?>();
        var epNum     = epNode?["number"]?.GetValue<int?>();

        var flags = vd["flags"]?.AsArray().Select(f => f?.GetValue<string>() ?? "").ToHashSet() ?? [];
        return new EpisodeRecording(
            ObjectId:        n["object_id"]!.GetValue<int>(),
            Path:            n["path"]!.GetValue<string>(),
            SeriesPath:      "",
            SeriesTitle:     showTitle,
            EpisodeTitle:    epTitle,
            Description:     epNode?["description"]?.GetValue<string>(),
            SeasonNumber:    season,
            EpisodeNumber:   epNum,
            OriginalAirDate: TryParseDate(epNode?["orig_air_date"]?.GetValue<string>()),
            AiredAt:         DateTimeOffset.Parse(ad["datetime"]?.GetValue<string>() ?? DateTimeOffset.UtcNow.ToString("o")),
            DurationSeconds: vd["duration"]?.GetValue<int>()  ?? 0,
            ScheduledSeconds:ad["duration"]?.GetValue<int>()  ?? 0,
            SizeBytes:       vd["size"]?.GetValue<long>()     ?? 0,
            State:           vd["state"]?.GetValue<string>() ?? "",
            NetworkName:     network,
            ChannelMajor:    major,
            ChannelMinor:    minor,
            ContainerFormat: vd["container_format"]?.GetValue<string>() ?? "mpeg2",
            Width:           vd["width"]?.GetValue<int>()     ?? 0,
            Height:          vd["height"]?.GetValue<int>()    ?? 0,
            AudioFormat:     vd["audio"]?.GetValue<string>()  ?? "",
            IsInterlaced:    flags.Contains("interlaced"),
            Watched:         n["user_info"]?["watched"]?.GetValue<bool>() ?? false
        );
    }

    private async Task<List<string>> GetIdArrayAsync(string path, CancellationToken ct)
    {
        try
        {
            var node = await LocalGetAsync(path, ct);
            if (node is JsonArray arr)
                return arr.Select(x => x?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList();
        }
        catch { }
        return [];
    }

    // ---------------------------------------------------------------------------
    // Guide / Live TV — real data methods
    // ---------------------------------------------------------------------------
    public async Task<List<Models.GuideChannel>> GetGuideChannelsAsync(CancellationToken ct = default)
    {
        var paths = await GetIdArrayAsync("/guide/channels", ct);
        var result = new List<Models.GuideChannel>(paths.Count);

        var tasks = paths.Select(async p =>
        {
            try
            {
                var n = await LocalGetAsync(p, ct);
                var ch = n["channel"]?["channel"] ?? n["channel"] ?? n;
                return new Models.GuideChannel(
                    Path:     p,
                    Major:    ch["major"]?.GetValue<int>()     ?? 0,
                    Minor:    ch["minor"]?.GetValue<int>()     ?? 0,
                    CallSign: ch["call_sign"]?.GetValue<string>() ?? "",
                    Network:  ch["network"]?.GetValue<string>()   ?? ""
                );
            }
            catch { return null; }
        });

        var fetched = await Task.WhenAll(tasks);
        result.AddRange(fetched.Where(c => c != null)!);
        result.Sort((a, b) => a.Major != b.Major ? a.Major.CompareTo(b.Major) : a.Minor.CompareTo(b.Minor));
        return result;
    }

    private static Models.GuideAiring? ParseGuideAiring(string path, System.Text.Json.Nodes.JsonNode n)
    {
        var ad = n["airing_details"];
        if (ad == null) return null;

        var datetimeStr = ad["datetime"]?.GetValue<string>();
        if (datetimeStr == null) return null;

        // Parse as UTC explicitly — Tablo returns "Z" UTC strings
        if (!DateTimeOffset.TryParse(datetimeStr,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var startsAt))
            return null;

        var epNode = n["episode"];
        var type   = path.Contains("/guide/series/") ? "series"
                   : path.Contains("/guide/movies/") ? "movie"
                   : path.Contains("/guide/sports/") ? "sport"
                   : "other";

        return new Models.GuideAiring(
            Path:            path,
            ChannelPath:     ad["channel_path"]?.GetValue<string>() ?? "",
            StartsAt:        startsAt,
            DurationSeconds: ad["duration"]?.GetValue<int>() ?? 0,
            ShowTitle:       ad["show_title"]?.GetValue<string>() ?? "",
            EpisodeTitle:    epNode?["title"]?.GetValue<string>() ?? "",
            Description:     epNode?["description"]?.GetValue<string>() ?? "",
            Type:            type,
            IsScheduled:     n["schedule"]?["state"]?.GetValue<string>() == "scheduled"
        );
    }

    public async Task ScheduleAiringAsync(string airingPath, CancellationToken ct = default)
    {
        Logger.Log($"ScheduleAiring: PATCH {airingPath}");
        await LocalPatchVoidAsync(airingPath,
            System.Text.Json.JsonSerializer.Serialize(new { scheduled = true }), ct);
        Logger.Log($"ScheduleAiring: SUCCESS");
    }

    public async Task UnscheduleAiringAsync(string airingPath, CancellationToken ct = default)
    {
        Logger.Log($"UnscheduleAiring: PATCH {airingPath}");
        await LocalPatchVoidAsync(airingPath,
            System.Text.Json.JsonSerializer.Serialize(new { scheduled = false }), ct);
        Logger.Log($"UnscheduleAiring: SUCCESS");
    }

    // Streams guide airings progressively — calls onBatch on each chunk so the UI
    // can populate rows without waiting for all 12,000+ airings to finish.
    // Tries a server-side time filter first (fast path). If unfiltered, loads
    // the current 6-hour window first, then continues the rest in background.
    public async Task StreamGuideAiringsAsync(
        Action<IEnumerable<Models.GuideAiring>> onBatch,
        IProgress<(int done, int total)>? progress = null,
        CancellationToken ct = default)
    {
        // Try time-filtered query — if the device supports it we get ~100 paths not 12k
        var now   = DateTimeOffset.UtcNow;
        var start = Uri.EscapeDataString(now.AddHours(-1).ToString("o"));
        var end   = Uri.EscapeDataString(now.AddHours(6).ToString("o"));
        List<string> windowPaths;
        try
        {
            windowPaths = await GetIdArrayAsync($"/guide/airings?start={start}&end={end}", ct);
        }
        catch { windowPaths = []; }

        List<string> allPaths;
        if (windowPaths.Count > 0 && windowPaths.Count < 2000)
        {
            // Server filtered — only load what was returned
            allPaths = windowPaths;
        }
        else
        {
            // Server returned everything (or filtering not supported) —
            // fall back to full list; we'll still stream progressively
            allPaths = await GetIdArrayAsync("/guide/airings", ct);
        }

        // Cap to first 2000 paths — the grid is fully populated well before this point
        if (allPaths.Count > 2000)
            allPaths = allPaths.Take(2000).ToList();

        // Try batch POST first (one request per 500 paths)
        bool batchWorked = false;
        try   { batchWorked = await TryStreamViaBatchAsync(allPaths, onBatch, progress, ct); }
        catch { }

        if (!batchWorked)
            await StreamViaIndividualGetAsync(allPaths, onBatch, progress, ct);
    }

    private async Task<bool> TryStreamViaBatchAsync(
        List<string> paths,
        Action<IEnumerable<Models.GuideAiring>> onBatch,
        IProgress<(int done, int total)>? progress,
        CancellationToken ct)
    {
        int done = 0;
        foreach (var chunk in paths.Chunk(500))
        {
            ct.ThrowIfCancellationRequested();
            var bodyJson = System.Text.Json.JsonSerializer.Serialize(chunk);
            var node     = await LocalPostAsync("/batch", bodyJson, ct);
            if (node is not System.Text.Json.Nodes.JsonObject obj) return false;

            var airings = new List<Models.GuideAiring>();
            foreach (var p in chunk)
            {
                try { var a = ParseGuideAiring(p, obj[p]!); if (a != null) airings.Add(a); } catch { }
                progress?.Report((++done, paths.Count));
            }
            onBatch(airings);
        }
        return true;
    }

    private async Task StreamViaIndividualGetAsync(
        List<string> paths,
        Action<IEnumerable<Models.GuideAiring>> onBatch,
        IProgress<(int done, int total)>? progress,
        CancellationToken ct)
    {
        int done = 0;
        foreach (var chunk in paths.Chunk(50))
        {
            ct.ThrowIfCancellationRequested();
            var results = await Task.WhenAll(chunk.Select(async p =>
            {
                try   { return ParseGuideAiring(p, await LocalGetAsync(p, ct)); }
                catch { return null; }
                finally { var d = System.Threading.Interlocked.Increment(ref done); progress?.Report((d, paths.Count)); }
            }));
            onBatch(results.Where(a => a != null)!);
        }
    }

    public async Task<string> GetLiveStreamUrlAsync(string channelPath, CancellationToken ct = default)
    {
        var n = await LocalPostAsync(channelPath + "/watch", "{}", ct);
        return n["playlist_url"]?.GetValue<string>() ?? n["url"]?.GetValue<string>() ?? "";
    }

    // ---------------------------------------------------------------------------
    // Guide / Live TV API discovery
    // Probes a set of candidate endpoints and returns a formatted report so we
    // can learn the actual shape of the guide API before building the real UI.
    // ---------------------------------------------------------------------------
    public async Task<string> ProbeGuideApiAsync(CancellationToken ct = default)
    {
        var sb = new System.Text.StringBuilder();
        var now = DateTimeOffset.UtcNow;
        sb.AppendLine($"Guide API probe — {now.LocalDateTime:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(new string('─', 60));

        // ── Phase 1: discover list endpoints ────────────────────────────
        sb.AppendLine("\n=== Phase 1: List endpoints ===");
        var listEndpoints = new[] { "/guide/channels", "/guide/airings" };
        var firstChannel = "";
        var firstAirings = new Dictionary<string, string>(); // type → first path

        foreach (var path in listEndpoints)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var node = await LocalGetAsync(path, ct);
                if (node is JsonArray arr)
                {
                    sb.AppendLine($"\n✓ GET {path}  ({arr.Count} items)");
                    foreach (var item in arr.Take(3))
                        sb.AppendLine($"  {item}");
                    if (arr.Count > 3) sb.AppendLine($"  …and {arr.Count - 3} more");

                    // Stash first paths for sampling
                    foreach (var item in arr)
                    {
                        var p = item?.GetValue<string>() ?? "";
                        if (path == "/guide/channels" && string.IsNullOrEmpty(firstChannel))
                            firstChannel = p;
                        if (path == "/guide/airings")
                        {
                            if (p.Contains("/guide/series/")  && !firstAirings.ContainsKey("series"))  firstAirings["series"]  = p;
                            if (p.Contains("/guide/movies/")  && !firstAirings.ContainsKey("movie"))   firstAirings["movie"]   = p;
                            if (p.Contains("/guide/sports/")  && !firstAirings.ContainsKey("sport"))   firstAirings["sport"]   = p;
                        }
                    }
                }
                else
                {
                    var json = JsonSerializer.Serialize(node, new JsonSerializerOptions { WriteIndented = true });
                    sb.AppendLine($"\n✓ GET {path}  (object)");
                    sb.AppendLine(json.Length > 600 ? json[..600] + "\n…" : json);
                }
            }
            catch (Exception ex) { sb.AppendLine($"\n✗ GET {path}  → {ex.Message}"); }
        }

        // ── Phase 2: sample one channel object ──────────────────────────
        sb.AppendLine("\n\n=== Phase 2: Sample objects ===");
        if (!string.IsNullOrEmpty(firstChannel))
        {
            await ProbeOnePath(sb, firstChannel, ct);
            // Also check if channels have their own airings list
            await ProbeOnePath(sb, firstChannel + "/airings", ct);
        }

        // ── Phase 3: sample one airing of each type ─────────────────────
        foreach (var (type, airingPath) in firstAirings)
        {
            ct.ThrowIfCancellationRequested();
            sb.AppendLine($"\n--- sample {type} airing ---");
            await ProbeOnePath(sb, airingPath, ct);
        }

        return sb.ToString();
    }

    private async Task ProbeOnePath(System.Text.StringBuilder sb, string path, CancellationToken ct)
    {
        try
        {
            var node = await LocalGetAsync(path, ct);
            var json = JsonSerializer.Serialize(node, new JsonSerializerOptions { WriteIndented = true });
            sb.AppendLine($"\n✓ GET {path}");
            sb.AppendLine(json.Length > 1800 ? json[..1800] + "\n…(truncated)" : json);
        }
        catch (Exception ex) { sb.AppendLine($"\n✗ GET {path}  → {ex.Message}"); }
    }

    // ---------------------------------------------------------------------------
    // Low-level HMAC-signed HTTP
    // ---------------------------------------------------------------------------

    // Retries up to maxRetries times on transient network errors (no response from device).
    // Does NOT retry on HTTP error status codes — those are deterministic failures.
    private async Task<JsonNode> LocalGetAsync(string path, CancellationToken ct, int maxRetries = 2)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var (req, _) = BuildLocalRequest(HttpMethod.Get, path, body: null);
                var resp = await _http.SendAsync(req, ct);
                var body = await resp.Content.ReadAsStringAsync(ct);
                if (!resp.IsSuccessStatusCode)
                    throw new HttpRequestException($"Device GET {path} → {resp.StatusCode}");
                return JsonNode.Parse(body) ?? throw new InvalidOperationException("Null JSON from " + path);
            }
            catch (HttpRequestException ex) when (ex.InnerException != null && attempt < maxRetries)
            {
                // InnerException present = transport failure (no response), not an HTTP error status.
                // Brief pause then retry.
                await Task.Delay(500 * (attempt + 1), ct);
            }
        }
    }

    private async Task<JsonNode> LocalPostAsync(string path, string jsonBody, CancellationToken ct)
    {
        var (req, _) = BuildLocalRequest(HttpMethod.Post, path, body: jsonBody);
        var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Device POST {path} → {resp.StatusCode}: {body}");
        return JsonNode.Parse(body) ?? throw new InvalidOperationException("Null JSON from " + path);
    }

    // Void variants — accept any 2xx, don't parse response body.
    private async Task LocalPostVoidAsync(string path, string jsonBody, CancellationToken ct)
    {
        var (req, _) = BuildLocalRequest(HttpMethod.Post, path, body: jsonBody);
        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Device POST {path} → {resp.StatusCode}: {body}");
        }
    }

    private async Task LocalPatchVoidAsync(string path, string jsonBody, CancellationToken ct)
    {
        var (req, _) = BuildLocalRequest(HttpMethod.Patch, path, body: jsonBody);
        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Device PATCH {path} → {resp.StatusCode}: {body}");
        }
    }

    private async Task LocalDeleteAsync(string path, CancellationToken ct)
    {
        var (req, _) = BuildLocalRequest(HttpMethod.Delete, path, body: null);
        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Device DELETE {path} → {resp.StatusCode}: {body}");
        }
    }

    private (HttpRequestMessage req, string date) BuildLocalRequest(HttpMethod method, string path, string? body)
    {
        if (!_auth.IsAuthenticated)
            throw new InvalidOperationException("Not authenticated. Call LoginAsync first.");

        var date    = DateTime.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ss") + " GMT";
        string bodyMd5 = "";

        if (body != null)
        {
            var md5Bytes = System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(body));
            bodyMd5 = Convert.ToHexString(md5Bytes).ToLowerInvariant();
        }

        var authHeader = TabloAuthService.MakeDeviceAuthHeader(method.Method, path, date, bodyMd5);
        var url = _auth.DeviceUrl!.TrimEnd('/') + path;

        var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("Authorization", authHeader);
        req.Headers.TryAddWithoutValidation("Date", date);
        req.Headers.TryAddWithoutValidation("Connection", "keep-alive");
        req.Headers.TryAddWithoutValidation("Accept", "*/*");
        req.Headers.TryAddWithoutValidation("User-Agent", TabloAuthService.DeviceUserAgent);

        if (body != null)
            req.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");

        return (req, date);
    }

    private static DateTimeOffset TryParseDate(string? s) =>
        DateTimeOffset.TryParse(s, out var d) ? d : DateTimeOffset.MinValue;
}
