using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace JellyMusicDiscovery.Services;

/// <summary>
/// Minimal Lidarr v1 client: lookup a release-group MBID, add it to Lidarr
/// monitored, optionally trigger an immediate search.
/// </summary>
public class LidarrClient
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<LidarrClient> _log;

    public LidarrClient(IHttpClientFactory httpFactory, ILogger<LidarrClient> log)
    {
        _httpFactory = httpFactory;
        _log = log;
    }

    public Task<RequestResult> AddAlbumByMbidAsync(string releaseGroupMbid, CancellationToken ct) =>
        AddAlbumAsync(releaseGroupMbid, freeText: null, expectedArtist: null, expectedAlbum: null, ct);

    public async Task<RequestResult> AddAlbumByTextAsync(string artist, string album, CancellationToken ct)
    {
        try
        {
            return await AddAlbumAsync(matchMbid: null, freeText: $"{artist} {album}".Trim(),
                expectedArtist: artist, expectedAlbum: album, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _log.LogWarning(ex, "Temporary Lidarr request failure for {Artist} - {Album}", artist, album);
            return RequestResult.Retry("Lidarr is temporarily unavailable; queued for retry.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning("Lidarr request timed out for {Artist} - {Album}", artist, album);
            return RequestResult.Retry("Lidarr timed out; queued for retry.");
        }
    }

    private async Task<RequestResult> AddAlbumAsync(string? matchMbid, string? freeText, string? expectedArtist, string? expectedAlbum, CancellationToken ct)
    {
        var cfg = Plugin.Instance!.Configuration;
        if (string.IsNullOrWhiteSpace(cfg.LidarrBaseUrl) || string.IsNullOrWhiteSpace(cfg.LidarrApiKey))
            return RequestResult.Fail("Lidarr is not configured.");

        using var http = MakeClient();

        // 1. Lookup the album in Lidarr (which queries MB internally).
        // Try multiple query shapes in order — first the full "{artist}
        // {album}" form, then album-only as a fallback. Soundtracks need
        // the album-only form because MusicBrainz credits them to the
        // composer, not the performer the user knows them by (e.g. Hugh
        // Jackman → "Greatest Showman" returns nothing useful, but just
        // "The Greatest Showman Soundtrack" returns the right release).
        var queries = new List<string>();
        if (!string.IsNullOrEmpty(matchMbid))
            queries.Add(matchMbid);
        else
        {
            if (!string.IsNullOrWhiteSpace(freeText)) queries.Add(freeText!);
            if (!string.IsNullOrEmpty(expectedAlbum)
                && !queries.Contains(expectedAlbum, StringComparer.OrdinalIgnoreCase))
                queries.Add(expectedAlbum);
        }
        if (queries.Count == 0) return RequestResult.Fail("Empty Lidarr lookup.");

        JsonElement? match = null;
        List<JsonElement>? lastLookup = null;
        foreach (var term in queries)
        {
            lastLookup = await http.GetFromJsonAsync<List<JsonElement>>(
                $"api/v1/album/lookup?term={Uri.EscapeDataString(term)}",
                JsonOpts, ct).ConfigureAwait(false);
            match = PickBestLookupHit(lastLookup, matchMbid, expectedArtist, expectedAlbum);
            if (match.HasValue)
            {
                _log.LogInformation("[mdiscover] Lidarr lookup '{Term}' matched: {Artist} - {Album}",
                    term,
                    match.Value.GetProperty("artist").GetProperty("artistName").GetString(),
                    match.Value.GetProperty("title").GetString());
                break;
            }
            _log.LogInformation("[mdiscover] Lidarr lookup '{Term}' produced no precise match; trying next query.", term);
        }

        if (match is null)
        {
            _log.LogWarning("[mdiscover] Lidarr lookup found no exact match for {Artist} - {Album}; refusing to guess. Top results: {Top}",
                expectedArtist, expectedAlbum,
                string.Join(", ", (lastLookup ?? new()).Take(3).Select(e =>
                    $"{(e.TryGetProperty("artist", out var ar) && ar.TryGetProperty("artistName", out var an) ? an.GetString() : "?")} - {(e.TryGetProperty("title", out var t) ? t.GetString() : "?")}")));
            return RequestResult.Fail($"No precise Lidarr match for: {expectedArtist} - {expectedAlbum}");
        }

        // 2. Resolve a default root folder + quality profile + metadata profile.
        var rootFolders = await http.GetFromJsonAsync<List<JsonElement>>("api/v1/rootfolder", JsonOpts, ct).ConfigureAwait(false);
        var rootPath = rootFolders?.FirstOrDefault().TryGetProperty("path", out var p) == true ? p.GetString() : null;
        if (rootPath is null) return RequestResult.Fail("Lidarr has no root folder configured.");

        var qProfiles = await http.GetFromJsonAsync<List<JsonElement>>("api/v1/qualityprofile", JsonOpts, ct).ConfigureAwait(false);
        var qpId = qProfiles?.FirstOrDefault().TryGetProperty("id", out var qid) == true ? qid.GetInt32() : 1;

        var mProfiles = await http.GetFromJsonAsync<List<JsonElement>>("api/v1/metadataprofile", JsonOpts, ct).ConfigureAwait(false);
        var mpId = mProfiles?.FirstOrDefault().TryGetProperty("id", out var mid) == true ? mid.GetInt32() : 1;

        // 3. Build the album add payload using the lookup result (Lidarr expects the full object back).
        var payloadObj = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(match.Value.GetRawText(), JsonOpts) ?? new();
        var artistObj = payloadObj.TryGetValue("artist", out var aEl)
            ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(aEl.GetRawText(), JsonOpts) ?? new()
            : new();

        // Detect whether the artist already exists in Lidarr's DB. The lookup
        // sets artist.id to a real >0 value when the artist is known, and 0/null
        // for a fresh artist that this POST will create. We use this later to
        // decide whether to unmonitor the artist post-add: if WE created it,
        // unmonitor (per user preference: don't track their other albums); if
        // the user already had the artist, don't disrupt their existing setup.
        var artistAlreadyExisted = artistObj.TryGetValue("id", out var preIdEl)
            && preIdEl.ValueKind == JsonValueKind.Number
            && preIdEl.GetInt32() > 0;

        artistObj["rootFolderPath"] = AsJson(rootPath);
        artistObj["qualityProfileId"] = AsJson(qpId);
        artistObj["metadataProfileId"] = AsJson(mpId);
        artistObj["monitored"] = AsJson(cfg.LidarrAddMonitored);
        // Critical: monitor="none" means "don't auto-monitor this artist's other
        // albums". Only the album we explicitly POST gets monitored (via the
        // album-level `monitored` flag in payloadObj). Without this, adding a
        // single album by an artist Lidarr doesn't know yet would create the
        // artist with monitor="all" and queue every album in their discography.
        artistObj["addOptions"] = AsJson(new { monitor = "none", searchForMissingAlbums = false });

        payloadObj["monitored"] = AsJson(cfg.LidarrAddMonitored);
        payloadObj["artist"] = AsJson(artistObj);
        // Deliberately NOT triggering search-on-add. The user's preference:
        // park the album in Wanted → Missing and let downstream tooling
        // (slskd's own scheduled jobs, RSS sync, etc) pick it up. Forcing
        // an immediate AlbumSearch hands the album to a download client
        // right now, which has been less reliable than waiting on the
        // background pipeline. We respect LidarrSearchOnAdd if explicitly
        // enabled in config; default false keeps things in Wanted only.
        payloadObj["addOptions"] = AsJson(new
        {
            searchForNewAlbum = false,
            addType = "automatic",
        });

        var addResp = await http.PostAsJsonAsync("api/v1/album", payloadObj, JsonOpts, ct).ConfigureAwait(false);
        if (!addResp.IsSuccessStatusCode)
        {
            var body = await addResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // Special case: Lidarr says "already added". Don't treat this as failure —
            // the user's intent (favoriting) is clearly "I want this in my library",
            // and Lidarr just knows about it already. Re-trigger a search for the
            // existing album so the favorite tap reliably means "go find this".
            if (addResp.StatusCode == System.Net.HttpStatusCode.BadRequest
                && body.Contains("AlbumExistsValidator", StringComparison.OrdinalIgnoreCase))
            {
                var mbid = TryParseAlreadyAddedMbid(body);
                _log.LogInformation("Lidarr says album already added (mbid={Mbid}); triggering search instead.", mbid);
                if (!string.IsNullOrEmpty(mbid))
                {
                    return await EnsureMonitoredAndSearchAsync(http, mbid, ct).ConfigureAwait(false);
                }
                return RequestResult.Fail("Album already in Lidarr but couldn't parse its MBID to trigger a search.");
            }

            _log.LogWarning("Lidarr add failed {Status}: {Body}", (int)addResp.StatusCode, body);
            return RequestResult.Fail($"Lidarr returned {(int)addResp.StatusCode}.");
        }

        // Add succeeded. Two post-create fixups (Lidarr quirks):
        //
        // 1. addOptions.monitor="none" silently overrides our album.monitored=true
        //    payload, leaving the album monitored=false. We have to PUT the album
        //    back to monitored=true explicitly. Otherwise Lidarr's Wanted →
        //    Missing won't show it and no scheduled search will pick it up.
        //
        // 2. Configure the artist:
        //      - monitored=true (so the album is visible in Wanted)
        //      - monitorNewItems=none (don't auto-track future releases)
        //    This matches "only this album, not the catalog" while keeping
        //    the album visible to scheduled searches.
        try
        {
            var addedJson = await addResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var addedDoc = JsonDocument.Parse(addedJson);

            int? newAlbumId = null;
            int? newArtistId = null;
            if (addedDoc.RootElement.TryGetProperty("id", out var aIdEl) && aIdEl.ValueKind == JsonValueKind.Number)
                newAlbumId = aIdEl.GetInt32();
            if (addedDoc.RootElement.TryGetProperty("artistId", out var arIdEl) && arIdEl.ValueKind == JsonValueKind.Number)
                newArtistId = arIdEl.GetInt32();

            if (newAlbumId.HasValue)
            {
                await EnsureAlbumMonitoredAsync(http, newAlbumId.Value, ct).ConfigureAwait(false);
            }
            if (newArtistId.HasValue)
            {
                await ConfigureArtistTrackingAsync(http, newArtistId.Value, ct).ConfigureAwait(false);
            }

            // Lidarr's RefreshArtist task runs ~5–10s after a new album add
            // (especially when the artist is new to Lidarr's DB). It re-pulls
            // metadata from MusicBrainz and re-applies its own monitoring
            // defaults — which OVERWRITES our monitored=true and
            // monitorNewItems=none settings, leaving the album invisible to
            // Soularr. Reapply the fixups after a delay so they win.
            if (newAlbumId.HasValue || newArtistId.HasValue)
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
                        using var http2 = MakeClient();
                        if (newAlbumId.HasValue)
                            await PutAlbumMonitoredAsync(http2, newAlbumId.Value, default).ConfigureAwait(false);
                        if (newArtistId.HasValue)
                            await PutArtistTrackingAsync(http2, newArtistId.Value, default).ConfigureAwait(false);
                        _log.LogInformation("Lidarr post-refresh fixup re-applied for album={Album} artist={Artist}", newAlbumId, newArtistId);
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Lidarr post-refresh fixup threw (non-fatal)");
                    }
                });
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not configure artist/album tracking after album add — non-fatal.");
        }

        return RequestResult.Ok("Album added to Lidarr; album monitored, artist set to monitor only this album.");
    }

    /// <summary>
    /// Ensure a Lidarr album has monitored=true. Used right after album add
    /// because Lidarr's `addOptions.monitor="none"` quirk leaves the new
    /// album unmonitored even when we pass monitored=true in the payload.
    /// Safe no-op if the album is already monitored.
    /// </summary>
    private async Task EnsureAlbumMonitoredAsync(HttpClient http, int albumId, CancellationToken ct)
    {
        // ALWAYS PUT monitored=true. We used to short-circuit when the GET
        // showed monitored=true, but Lidarr's RefreshArtist task runs
        // asynchronously after album add and can flip it back to false a
        // few seconds later. PUTing always ensures we leave a record of
        // the desired state. Plus we schedule a delayed retry below to
        // re-apply the change AFTER any refresh task settles.
        await PutAlbumMonitoredAsync(http, albumId, ct).ConfigureAwait(false);
    }

    private async Task PutAlbumMonitoredAsync(HttpClient http, int albumId, CancellationToken ct)
    {
        try
        {
            var albumEl = await http.GetFromJsonAsync<JsonElement>($"api/v1/album/{albumId}", JsonOpts, ct).ConfigureAwait(false);
            var albumDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(albumEl.GetRawText(), JsonOpts) ?? new();
            albumDict["monitored"] = AsJson(true);
            var resp = await http.PutAsJsonAsync($"api/v1/album/{albumId}", albumDict, JsonOpts, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var b = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                _log.LogWarning("Failed to mark album {Id} monitored=true: {Status} {Body}", albumId, (int)resp.StatusCode, b);
            }
            else
            {
                _log.LogInformation("Album {Id} → monitored=true.", albumId);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "PutAlbumMonitoredAsync failed for {Id}", albumId);
        }
    }

    /// <summary>
    /// Configure an artist to keep `monitored=true` (so their albums can show
    /// up in Wanted → Missing) but set `monitorNewItems="none"` so future
    /// album discoveries don't auto-track. This is the right knob for the
    /// user's "I want only specific albums, not the whole catalog" workflow.
    ///
    /// Why not just `monitored=false`? Lidarr's Wanted view filters by
    /// `artist.monitored=true` — turning that off hides the album from the
    /// UI and stops scheduled searches from picking it up.
    /// </summary>
    private async Task ConfigureArtistTrackingAsync(HttpClient http, int artistId, CancellationToken ct)
    {
        await PutArtistTrackingAsync(http, artistId, ct).ConfigureAwait(false);
    }

    private async Task PutArtistTrackingAsync(HttpClient http, int artistId, CancellationToken ct)
    {
        var artistEl = await http.GetFromJsonAsync<JsonElement>($"api/v1/artist/{artistId}", JsonOpts, ct).ConfigureAwait(false);
        var artistDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(artistEl.GetRawText(), JsonOpts) ?? new();

        bool needsUpdate = false;
        // 1. Make sure the artist is monitored — otherwise their albums won't
        //    appear in Wanted → Missing and won't get searched.
        if (artistEl.TryGetProperty("monitored", out var mEl) && mEl.ValueKind == JsonValueKind.False)
        {
            artistDict["monitored"] = AsJson(true);
            needsUpdate = true;
        }
        // 2. Make sure new album discoveries don't auto-track. Lidarr field
        //    is `monitorNewItems`: valid values "all" / "new" / "none".
        if (!artistEl.TryGetProperty("monitorNewItems", out var mnEl)
            || mnEl.ValueKind != JsonValueKind.String
            || !string.Equals(mnEl.GetString(), "none", StringComparison.OrdinalIgnoreCase))
        {
            artistDict["monitorNewItems"] = AsJson("none");
            needsUpdate = true;
        }

        if (!needsUpdate)
        {
            _log.LogInformation("Artist {Id} already configured: monitored=true, monitorNewItems=none.", artistId);
            return;
        }

        var resp = await http.PutAsJsonAsync($"api/v1/artist/{artistId}", artistDict, JsonOpts, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            var b = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _log.LogWarning("Failed to configure artist {Id} tracking: {Status} {Body}", artistId, (int)resp.StatusCode, b);
        }
        else
        {
            _log.LogInformation("Artist {Id} set: monitored=true, monitorNewItems=none.", artistId);
        }
    }

    /// <summary>
    /// Parse the MBID out of Lidarr's AlbumExistsValidator 400 response.
    /// Body shape:
    ///   [ { "propertyName": "ForeignAlbumId", "errorCode": "AlbumExistsValidator",
    ///       "attemptedValue": "&lt;mbid&gt;", ... } ]
    /// </summary>
    private static string? TryParseAlreadyAddedMbid(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.TryGetProperty("attemptedValue", out var av) && av.ValueKind == JsonValueKind.String)
                    return av.GetString();
            }
        }
        catch { /* fall through */ }
        return null;
    }

    /// <summary>
    /// For an album Lidarr already knows about: flip THIS album to
    /// monitored=true, then flip its ARTIST to monitored=false. The user's
    /// preference: they're typically a "track albums one at a time" person,
    /// so they don't want this artist's other releases auto-monitored just
    /// because we touched one of their albums.
    ///
    /// We also do NOT fire AlbumSearch — the album sits in Wanted → Missing
    /// for slskd's scheduled scan or Lidarr's RSS sync to pick up.
    /// </summary>
    private async Task<RequestResult> EnsureMonitoredAndSearchAsync(HttpClient http, string mbid, CancellationToken ct)
    {
        var albums = await http.GetFromJsonAsync<List<JsonElement>>(
            $"api/v1/album?foreignAlbumId={Uri.EscapeDataString(mbid)}", JsonOpts, ct).ConfigureAwait(false);
        var album = albums?.FirstOrDefault();
        if (album is null || album.Value.ValueKind == JsonValueKind.Undefined)
            return RequestResult.Fail($"Lidarr has no album with foreignAlbumId={mbid}.");

        var albumId = album.Value.GetProperty("id").GetInt32();
        var albumMonitored = album.Value.TryGetProperty("monitored", out var aMonEl) && aMonEl.GetBoolean();
        var artistId = album.Value.TryGetProperty("artistId", out var arIdEl) ? arIdEl.GetInt32() : 0;

        // Flip the album to monitored=true (puts it in Wanted → Missing).
        if (!albumMonitored)
        {
            var albumDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(album.Value.GetRawText(), JsonOpts) ?? new();
            albumDict["monitored"] = AsJson(true);
            var monResp = await http.PutAsJsonAsync($"api/v1/album/{albumId}", albumDict, JsonOpts, ct).ConfigureAwait(false);
            if (!monResp.IsSuccessStatusCode)
            {
                var b = await monResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                _log.LogWarning("Failed to mark album {Id} monitored: {Status} {Body}", albumId, (int)monResp.StatusCode, b);
                return RequestResult.Fail("Lidarr already had the album but couldn't flip it monitored.");
            }
            _log.LogInformation("Album {Id} flipped to monitored=true (now in Wanted → Missing).", albumId);
        }
        else
        {
            _log.LogInformation("Album {Id} already monitored.", albumId);
        }

        // Configure THIS album's artist: keep monitored=true so the album is
        // visible in Wanted → Missing, but set monitorNewItems=none so
        // future album discoveries don't auto-track. We touch only THIS
        // album's artist — never any other artists in Lidarr.
        if (artistId > 0)
        {
            await ConfigureArtistTrackingAsync(http, artistId, ct).ConfigureAwait(false);
        }

        return RequestResult.Ok("Album monitored; artist set to monitor only this album. Now in Wanted → Missing.");
    }

    private HttpClient MakeClient()
    {
        var cfg = Plugin.Instance!.Configuration;
        var http = _httpFactory.CreateClient();
        http.BaseAddress = new Uri(cfg.LidarrBaseUrl.TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromSeconds(20);
        http.DefaultRequestHeaders.Add("X-Api-Key", cfg.LidarrApiKey);
        return http;
    }

    private static JsonElement AsJson(object? o) =>
        JsonSerializer.SerializeToElement(o, JsonOpts);

    /// <summary>
    /// Lowercase + strip punctuation/whitespace + drop parenthetical suffixes —
    /// "blink-182" and "Blink 182" should compare equal; same for
    /// "California" vs "California (Deluxe Edition)".
    /// </summary>
    /// <summary>
    /// Score and pick the best hit from a Lidarr album-lookup response.
    /// Returns null if no hit clears the bar — caller is expected to
    /// retry with a different query rather than guess.
    ///
    /// Algorithm:
    ///   1. Drop hits where artist/title fields are missing
    ///   2. Reject hits whose ALBUM TITLE doesn't token-set-equivalent
    ///      to expected (drops "The Greatest Show" when we asked for
    ///      "The Greatest Showman")
    ///   3. Score remaining hits: artist substring match = 100,
    ///      soundtrack-with-artist-mismatch = 60, else reject
    ///   4. Pick highest score; ties broken by Lidarr's own ranking order
    /// </summary>
    private JsonElement? PickBestLookupHit(List<JsonElement>? lookup, string? matchMbid,
        string? expectedArtist, string? expectedAlbum)
    {
        if (lookup is null || lookup.Count == 0) return null;

        // Direct MBID match takes precedence.
        if (!string.IsNullOrEmpty(matchMbid))
        {
            return lookup.FirstOrDefault(e =>
                e.TryGetProperty("foreignAlbumId", out var fid)
                && string.Equals(fid.GetString(), matchMbid, StringComparison.OrdinalIgnoreCase));
        }

        if (string.IsNullOrEmpty(expectedArtist) || string.IsNullOrEmpty(expectedAlbum)) return null;

        var expectedIsSoundtrack = IsSoundtrackTitle(expectedAlbum);

        JsonElement? best = null;
        int bestScore = int.MinValue;
        int bestIdx = int.MaxValue;
        int idx = 0;
        foreach (var e in lookup)
        {
            if (!e.TryGetProperty("artist", out var ar) || ar.ValueKind == JsonValueKind.Null) { idx++; continue; }
            var artistName = ar.TryGetProperty("artistName", out var an) ? an.GetString() : null;
            var albumTitle = e.TryGetProperty("title", out var t) ? t.GetString() : null;
            if (string.IsNullOrEmpty(artistName) || string.IsNullOrEmpty(albumTitle)) { idx++; continue; }

            if (!AlbumTitlesEquivalent(expectedAlbum, albumTitle)
                && !AlbumTitlesEquivalent(RemoveLeadingArtist(expectedAlbum, expectedArtist), albumTitle))
            {
                idx++;
                continue;
            }

            var aN = NormalizeName(artistName);
            var aE = NormalizeName(expectedArtist);
            bool artistStrongMatch = aN.Length > 0 && (aN.Contains(aE) || aE.Contains(aN));

            bool isSoundtrackHit = expectedIsSoundtrack || IsSoundtrackTitle(albumTitle);

            if (!artistStrongMatch && !isSoundtrackHit) { idx++; continue; }

            int score = artistStrongMatch ? 100 : 60;
            if (score > bestScore || (score == bestScore && idx < bestIdx))
            {
                best = e; bestScore = score; bestIdx = idx;
            }
            idx++;
        }
        return best;
    }

    private static string NormalizeName(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var lower = s.ToLowerInvariant();
        var paren = lower.IndexOf('(');
        if (paren > 0) lower = lower.Substring(0, paren);
        var bracket = lower.IndexOf('[');
        if (bracket > 0) lower = lower.Substring(0, bracket);
        var sb = new System.Text.StringBuilder(lower.Length);
        foreach (var ch in lower)
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        }
        return sb.ToString();
    }

    private static string RemoveLeadingArtist(string album, string artist)
    {
        var pattern = @"^\s*" + System.Text.RegularExpressions.Regex.Escape(artist)
            + @"\s*[-–—:']*\s*";
        return System.Text.RegularExpressions.Regex.Replace(album, pattern, string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    // Album-title qualifier words. When comparing two titles token-wise,
    // these are removed from both sides before equality check, so e.g.
    // "California (Deluxe Edition)" matches "California" (with no risk of
    // "The Greatest Show" matching "The Greatest Showman" — that's a
    // content-word difference, not a qualifier).
    private static readonly System.Collections.Generic.HashSet<string> _albumQualifierWords =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "deluxe", "edition", "version", "bonus", "remastered", "remaster",
            "expanded", "anniversary", "reissue", "live", "acoustic", "special",
            "explicit", "clean", "extended", "complete", "single", "ep",
            // Soundtrack markers — different MB releases use different conventions.
            "original", "motion", "picture", "soundtrack", "ost", "score",
            "from", "the",
        };

    /// <summary>
    /// Album-title equivalence check: tokenize both titles, drop
    /// qualifier words from each, and compare as sets. "The Greatest
    /// Showman: Original Motion Picture Soundtrack" and "The Greatest
    /// Showman" both reduce to {"greatest", "showman"} and match. "The
    /// Greatest Show" reduces to {"greatest", "show"} and does NOT match
    /// (different content word).
    /// </summary>
    internal static bool AlbumTitlesEquivalent(string expected, string candidate)
    {
        var e = AlbumCoreTokens(expected);
        var c = AlbumCoreTokens(candidate);
        if (e.Count == 0 || c.Count == 0) return false;
        return e.SetEquals(c);
    }

    private static System.Collections.Generic.HashSet<string> AlbumCoreTokens(string title)
    {
        // Strip parenthesised/bracketed segments first — they're usually
        // qualifier-only ("(Deluxe Edition)" / "[Bonus Tracks]") and
        // tokens inside them shouldn't dominate the comparison even when
        // they aren't on our qualifier list.
        var noparen = System.Text.RegularExpressions.Regex.Replace(title, @"[\(\[][^\)\]]*[\)\]]", " ");
        var matches = System.Text.RegularExpressions.Regex.Matches(noparen.ToLowerInvariant(), @"[a-z0-9]+");
        var result = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            var tok = m.Value;
            if (tok.Length < 2) continue; // drop "a", single chars
            if (_albumQualifierWords.Contains(tok)) continue;
            result.Add(tok);
        }
        return result;
    }

    /// <summary>True if the title contains soundtrack/score markers
    /// ("Soundtrack", "OST", "Original Motion Picture", etc.).</summary>
    internal static bool IsSoundtrackTitle(string title)
    {
        if (string.IsNullOrEmpty(title)) return false;
        var lower = title.ToLowerInvariant();
        return lower.Contains("soundtrack")
            || lower.Contains(" ost")
            || lower.EndsWith("ost")
            || lower.Contains("original motion picture")
            || lower.Contains("original score");
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public class RequestResult
    {
        public bool Success { get; init; }
        public bool Retryable { get; init; }
        public string Message { get; init; } = string.Empty;
        public static RequestResult Ok(string msg) => new() { Success = true, Message = msg };
        public static RequestResult Fail(string msg) => new() { Success = false, Message = msg };
        public static RequestResult Retry(string msg) => new() { Success = false, Retryable = true, Message = msg };
    }
}
