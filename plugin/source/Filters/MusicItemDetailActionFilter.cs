using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JellyMusicDiscovery.Services;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace JellyMusicDiscovery.Filters;

/// <summary>
/// Makes click-through work for stub items.
///
/// 1) GET /Users/{userId}/Items/{itemId}: Jellyfin returns 404 if the id isn't
///    in its DB. We synthesize a BaseItemDto from the discovery cache so the
///    client can render the artist/album page.
/// 2) GET /Items?ParentId={stubGuid}: Jellyfin returns 0 children. For album
///    stubs we fetch the Deezer tracklist on demand and inject the children;
///    for artist stubs we'd inject albums (left as TODO; the album route works
///    via the search page already so it's not a blocker).
/// </summary>
public class MusicItemDetailActionFilter : IAsyncActionFilter, IAsyncResultFilter
{
    private readonly DiscoveryItemCache _cache;
    private readonly DeezerClient _deezer;
    private readonly LidarrClient _lidarr;
    private readonly SlskdClient _slskd;
    private readonly SlskdDownloadFinisher _slskdFinisher;
    private readonly LrcLibClient _lrclib;
    private readonly LibraryRegistrar _registrar;
    private readonly RecentlyPlayedTracker _recents;
    private readonly PlaylistStubLinks _playlistLinks;
    private readonly DiscoveryPlaylistFeed _discoveryFeed;
    private readonly System.Net.Http.IHttpClientFactory _httpFactory;
    private readonly MediaBrowser.Controller.Library.ILibraryManager _libraryManager;
    private readonly MediaBrowser.Controller.Session.ISessionManager _sessionManager;
    private readonly MediaBrowser.Controller.Net.IAuthorizationContext _authContext;
    private readonly ILogger<MusicItemDetailActionFilter> _log;
    // Per-session state for delayed-nudge logic. Finer aggressively prefetches
    // upcoming audio while the current track is still playing — if we fire
    // OnPlaybackStart immediately on an audio fetch, the WebSocket update
    // arrives while the OLD track is still actually playing, and the client
    // has nothing to react to when the gapless transition actually happens.
    // We defer the nudge for prefetched tracks until the current track's
    // expected end time.
    private record SessionPlaybackState(Guid TrackId, DateTime StartedAt, TimeSpan Runtime);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SessionPlaybackState> _sessionCurrentTrack = new();
    // Dedupe Lidarr triggers — we only fire one add per album per process lifetime.
    // Soft per-album rate limit so the same favorite tap-tap-tap doesn't spam
    // Lidarr. We only suppress repeats within a short window — long enough to
    // dedupe "tap tap" double-clicks, short enough that re-favoriting later
    // (e.g. user wants to retry) actually fires.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, DateTime> _lidarrLastFiredAt = new();
    private static readonly TimeSpan _lidarrFireCooldown = TimeSpan.FromSeconds(15);

    public MusicItemDetailActionFilter(
        DiscoveryItemCache cache,
        DeezerClient deezer,
        LidarrClient lidarr,
        SlskdClient slskd,
        SlskdDownloadFinisher slskdFinisher,
        LrcLibClient lrclib,
        LibraryRegistrar registrar,
        RecentlyPlayedTracker recents,
        PlaylistStubLinks playlistLinks,
        DiscoveryPlaylistFeed discoveryFeed,
        System.Net.Http.IHttpClientFactory httpFactory,
        MediaBrowser.Controller.Library.ILibraryManager libraryManager,
        MediaBrowser.Controller.Session.ISessionManager sessionManager,
        MediaBrowser.Controller.Net.IAuthorizationContext authContext,
        ILogger<MusicItemDetailActionFilter> log)
    {
        _cache = cache;
        _deezer = deezer;
        _lidarr = lidarr;
        _slskd = slskd;
        _slskdFinisher = slskdFinisher;
        _lrclib = lrclib;
        _registrar = registrar;
        _recents = recents;
        _playlistLinks = playlistLinks;
        _discoveryFeed = discoveryFeed;
        _httpFactory = httpFactory;
        _libraryManager = libraryManager;
        _sessionManager = sessionManager;
        _authContext = authContext;
        _log = log;
    }

    // --- Pre-action: short-circuit single-item lookups + ParentId queries  ---
    // We have to intervene here, not in OnResultExecutionAsync, because Jellyfin's
    // controller validates ParentId against the library before any result is set
    // and throws ArgumentException("Invalid parent id") for stub Guids.
    public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
    {
        try
        {
            // Trace every fire so we can see exactly what's happening when the album page loads.
            _log.LogInformation("[mdiscover] action fire: {Method} {Path}{Query}",
                ctx.HttpContext.Request.Method, ctx.HttpContext.Request.Path, ctx.HttpContext.Request.QueryString);

            // (a-pre-pre) Playlist endpoints. PlaylistManager silently filters
            // out our stubs even though they're registered — so we bypass it
            // entirely for /Playlists/{id}/Items POSTs. We:
            //   1. Look up the playlist via _libraryManager
            //   2. For each id that maps to a stub track, build a LinkedChild
            //   3. Append to playlist.LinkedChildren
            //   4. Save via UpdateToRepositoryAsync
            //   5. Return 204 No Content (short-circuit)
            // For other playlist endpoints (POST /Playlists, etc.) we still do
            // the registration nudge in case Jellyfin's path works.
            if (await TryHandleAddItemsToPlaylistAsync(ctx).ConfigureAwait(false))
            {
                return;
            }
            // Track stub removals from playlists (don't short-circuit; Jellyfin
            // may need to handle real-item removals from the same request).
            TryHandleRemoveItemsFromPlaylist(ctx);
            EnsureStubsRegisteredForPlaylistRequest(ctx);

            // (a-pre) /Audio/{id}/Lyrics or /Items/{id}/Lyrics — serve LRC LIB
            // synced lyrics for stub tracks. The LrcLib Jellyfin plugin only
            // hooks into the normal library/provider pipeline; our stubs
            // bypass that, so we look up lyrics directly via lrclib.net here.
            if (IsLyricsRoute(ctx, out var lyId)
                && _cache.TryGet(lyId, out var lyEntry) && lyEntry is { Kind: "track" })
            {
                _registrar.RegisterStubTrack(lyId, lyEntry);
                _log.LogInformation("[mdiscover] Lyrics request for stub track {Id}: {Artist} - {Title}",
                    lyId, lyEntry.Artist, lyEntry.TrackTitle);
                var dto = await BuildLyricDtoAsync(lyEntry, ctx.HttpContext.RequestAborted).ConfigureAwait(false);
                if (dto is null)
                {
                    // 404 keeps clients quiet rather than showing "lyrics failed".
                    ctx.Result = new NotFoundResult();
                }
                else
                {
                    ctx.Result = new ObjectResult(dto);
                }
                return;
            }

            // (a0) PlaybackInfo on a stub track → synthesize PlaybackInfoResponse pointing at our stream endpoint.
            // The web client hits /Items/{id}/PlaybackInfo when the user clicks play; if the id isn't a real
            // library item Jellyfin returns 404 and the player shows "There was an error processing the request."
            if (IsPlaybackInfoRoute(ctx) && TryExtractItemIdFromPath(ctx, out var pbId)
                && _cache.TryGet(pbId, out var pbEntry) && pbEntry is { Kind: "track" })
            {
                _registrar.RegisterStubTrack(pbId, pbEntry);
                _log.LogInformation("[mdiscover] PlaybackInfo synthesized for stub track {Id}", pbId);
                ctx.Result = new ObjectResult(BuildPlaybackInfoForTrack(pbId, pbEntry));
                return;
            }

            // (a0v) Same idea for synthetic MusicVideo stubs — the web video
            // player negotiates via PlaybackInfo POST and abandons playback if
            // the server returns empty MediaSources. Synthesize a video-shaped
            // response pointing at /Videos/{id}/... which our stream resolver
            // 302s to the Python /video/by-id endpoint.
            if (IsPlaybackInfoRoute(ctx) && TryExtractItemIdFromPath(ctx, out var pbVid)
                && _cache.TryGet(pbVid, out var pbVEntry) && pbVEntry is { Kind: "video" })
            {
                _registrar.RegisterStubMusicVideo(pbVid, pbVEntry);
                _log.LogInformation("[mdiscover] PlaybackInfo synthesized for stub video {Id}", pbVid);
                ctx.Result = new ObjectResult(BuildPlaybackInfoForVideo(pbVid, pbVEntry));
                return;
            }

            // /Sessions/Playing(/Progress|/Stopped|/Ping) for stub tracks. Jellyfin
            // 404s these because the itemId isn't in the library — and the client
            // ends up with a stale "now playing" state on auto-advance, so the UI
            // shows the previous track's title and artwork even though the audio
            // moved on. Intercept and return 204 No Content so the client gets
            // a clean success.
            if (IsSessionsPlayingRoute(ctx) && TryExtractSessionItemId(ctx, out var sesId)
                && _cache.TryGet(sesId, out var sesEntry) && sesEntry is { Kind: "track" })
            {
                _log.LogInformation("[mdiscover] Sessions/Playing intercepted for stub track {Id} ({Path})",
                    sesId, ctx.HttpContext.Request.Path);
                ctx.Result = new NoContentResult();
                return;
            }

            // Favorite/star endpoint → explicit "save to library" signal. We trigger
            // Lidarr only when the user takes a deliberate action (tapping the heart),
            // not on every play. Far more discoverable across clients (web, Finer, iOS
            // all expose a heart icon) and lines up with how users think about
            // "I want this in my collection" vs. "I'm just listening once".
            //
            // Two flavors of "favorite this":
            //   1. URL-based: POST /Users/.../FavoriteItems/{itemId}      (web client)
            //   2. Body-based: POST /UserItems/{itemId}/UserData          (Finer / iOS)
            //                  with body { "IsFavorite": true, ... }
            // The body-based form has no "favorite" in the URL — we have to peek at
            // the action arguments after model binding to know it was a favorite.
            if (IsFavoriteRoute(ctx, out var favId))
            {
                if (_cache.TryGet(favId, out var favEntry) && favEntry is not null)
                {
                    var isPost = HttpMethods.IsPost(ctx.HttpContext.Request.Method);
                    _log.LogInformation("[mdiscover] favorite tapped (URL, {Method}, stub): {Id} kind={Kind}",
                        ctx.HttpContext.Request.Method, favId, favEntry.Kind);
                    FireLidarrAddIfFavorited(favEntry);
                    ctx.Result = new ObjectResult(new MediaBrowser.Model.Dto.UserItemDataDto
                    {
                        ItemId = favId,
                        IsFavorite = isPost,
                        Played = false,
                        Key = favId.ToString("N"),
                    });
                    return;
                }

                // Cache miss. Two possibilities:
                //   a) it's a real Jellyfin album (e.g. user has 1 song from
                //      that album physically; Jellyfin has a partial album).
                //      We can still help: look up the item, get artist+title,
                //      ask Lidarr to fill it in.
                //   b) it's a stale stub from a session before our last restart.
                //      Library lookup will return null. Log + fall through.
                if (TryFireLidarrFromLibraryItem(favId))
                {
                    // Don't short-circuit — let Jellyfin save the favorite normally
                    // (the album is real, so the favorite genuinely applies).
                }
                else
                {
                    _log.LogWarning("[mdiscover] favorite tapped on UNKNOWN id {Id} — neither in our stub cache nor in Jellyfin's library. Likely a stale client cache from before a restart.", favId);
                }
            }

            if (IsUserDataUpdateRoute(ctx, out var udId, out var udIsFav))
            {
                if (_cache.TryGet(udId, out var udEntry) && udEntry is not null)
                {
                    _log.LogInformation("[mdiscover] UserData update (stub): {Id} kind={Kind} isFavorite={Fav}", udId, udEntry.Kind, udIsFav);
                    if (udIsFav)
                    {
                        FireLidarrAddIfFavorited(udEntry);
                    }
                    // Short-circuit for stub items — Jellyfin's SaveUserData
                    // would throw "Invalid item id" otherwise.
                    ctx.Result = new ObjectResult(new MediaBrowser.Model.Dto.UserItemDataDto
                    {
                        ItemId = udId,
                        IsFavorite = udIsFav,
                        Played = false,
                        Key = udId.ToString("N"),
                    });
                    return;
                }

                // Real Jellyfin item with IsFavorite=true → fire Lidarr.
                if (udIsFav && TryFireLidarrFromLibraryItem(udId))
                {
                    // Let Jellyfin save user data normally.
                }
            }

            // (a1) /Audio/{id}/* — the audio fetch endpoints. Two flows:
            //
            //   1. /Audio/{id}/universal with EnableRedirection=true
            //      → web client EXPECTS a 302 redirect to a direct-stream URL.
            //        Returning bytes directly here makes web's audio element
            //        spin forever (it's waiting for the redirect that never
            //        comes). Reply 302 → /Audio/{id}/stream.mp3.
            //
            //   2. /Audio/{id}/stream.mp3 (or /stream, /File, /Download, etc.)
            //      → return audio bytes proxied from ytmusic-stream-server.
            //
            // Native clients (Finer iOS) just hit /Items/{id}/File directly
            // and never go through /universal, so they only see flow 2.
            // Video stream — synthetic MusicVideo stub. Hits hit any of:
            //   /Videos/{id}/stream         (web)
            //   /Videos/{id}/stream.mp4     (web with format hint)
            //   /Items/{id}/Download        (Finer/iOS)
            //   /Videos/{id}/master.m3u8    (HLS — we don't support, fail fast)
            // We just 302 to the Python /video/by-id/{vid} endpoint, which
            // resolves a single-URL progressive MP4 via yt-dlp and 302s to it.
            if (IsVideoStreamRoute(ctx) && TryExtractItemIdFromPath(ctx, out var vId)
                && _cache.TryGet(vId, out var vEntry) && vEntry is { Kind: "video" })
            {
                var cfg = Plugin.Instance!.Configuration;
                if (string.IsNullOrWhiteSpace(cfg.YtMusicStreamServerUrl))
                {
                    _log.LogWarning("[mdiscover] /Videos stream for stub but YtMusicStreamServerUrl not configured");
                }
                else if (string.IsNullOrEmpty(vEntry.VideoId))
                {
                    _log.LogWarning("[mdiscover] /Videos stream for {Id} but VideoId missing", vId);
                }
                else
                {
                    var target = $"{cfg.YtMusicStreamServerUrl.TrimEnd('/')}/video/by-id/{vEntry.VideoId}";
                    _log.LogInformation("[mdiscover] /Videos redirect: {Id} '{Title}' → {Url}", vId, vEntry.TrackTitle, target);
                    ctx.Result = new RedirectResult(target, permanent: false);
                    return;
                }
            }

            if (IsAudioStreamRoute(ctx) && TryExtractItemIdFromPath(ctx, out var aId)
                && _cache.TryGet(aId, out var aEntry) && aEntry is { Kind: "track" })
            {
                // (radio) Internet radio station — short-circuit straight
                // to the upstream stream with a 302. Bypasses the YT proxy
                // entirely, so radio works even without YtMusicStreamServerUrl
                // configured.
                //
                // Honour the same /universal contract the YT path does:
                // when the web client hits /Audio/{id}/universal without
                // EnableRedirection=true, it expects audio BYTES, not a
                // 302. We bounce to /stream.mp3 first; on the next call
                // (which lands in this same branch) we 302 to the upstream.
                if (!string.IsNullOrEmpty(aEntry.RadioStreamUrl))
                {
                    var rawPath = ctx.HttpContext.Request.Path.Value ?? string.Empty;
                    bool isUniversal = rawPath.IndexOf("/universal", StringComparison.OrdinalIgnoreCase) > 0;
                    if (isUniversal)
                    {
                        var redirTarget = $"/Audio/{aId:N}/main.mp3";
                        if (ctx.HttpContext.Request.Query.TryGetValue("ApiKey", out var rak)
                            || ctx.HttpContext.Request.Query.TryGetValue("api_key", out rak))
                        {
                            redirTarget += $"?api_key={Uri.EscapeDataString(rak.ToString())}";
                        }
                        _log.LogInformation("[mdiscover] /Audio radio universal-bounce: {Id} → {Target}", aId, redirTarget);
                        ctx.Result = new RedirectResult(redirTarget, permanent: false);
                        return;
                    }

                    _log.LogInformation("[mdiscover] /Audio radio redirect: {Id} '{Name}' → {Url}",
                        aId, aEntry.TrackTitle, aEntry.RadioStreamUrl);
                    // Same session-nudge pattern as the YT path so clients
                    // refresh their now-playing UI when starting a station.
                    _ = NudgeSessionPlaybackStartAsync(ctx.HttpContext, aId, aEntry);
                    ctx.Result = new RedirectResult(aEntry.RadioStreamUrl!, permanent: false);
                    return;
                }

                var cfg = Plugin.Instance!.Configuration;
                if (string.IsNullOrWhiteSpace(cfg.YtMusicStreamServerUrl))
                {
                    _log.LogWarning("[mdiscover] /Audio stream for stub but YtMusicStreamServerUrl not configured");
                }
                else
                {
                    var rawPath = ctx.HttpContext.Request.Path.Value ?? string.Empty;
                    bool isUniversal = rawPath.IndexOf("/universal", StringComparison.OrdinalIgnoreCase) > 0;
                    bool wantsRedirect = isUniversal &&
                        ctx.HttpContext.Request.Query.TryGetValue("EnableRedirection", out var redirVal) &&
                        string.Equals(redirVal.ToString(), "true", StringComparison.OrdinalIgnoreCase);

                    if (wantsRedirect)
                    {
                        var redirTarget = $"/Audio/{aId:N}/stream.mp3";
                        // Forward the api_key so the redirect target stays auth'd.
                        if (ctx.HttpContext.Request.Query.TryGetValue("ApiKey", out var ak)
                            || ctx.HttpContext.Request.Query.TryGetValue("api_key", out ak))
                        {
                            redirTarget += $"?api_key={Uri.EscapeDataString(ak.ToString())}";
                        }
                        _log.LogInformation("[mdiscover] /Audio universal redirect: {Id} → {Target}", aId, redirTarget);
                        ctx.Result = new RedirectResult(redirTarget, permanent: false);
                        return;
                    }

                    var qs = $"artist={Uri.EscapeDataString(aEntry.Artist ?? string.Empty)}&title={Uri.EscapeDataString(aEntry.TrackTitle ?? string.Empty)}";
                    var upstreamUrl = $"{cfg.YtMusicStreamServerUrl.TrimEnd('/')}/stream?{qs}";
                    _log.LogInformation("[mdiscover] /Audio proxy: {Artist} - {Title} ← {Url}", aEntry.Artist, aEntry.TrackTitle, upstreamUrl);

                    // Fire-and-forget: nudge Jellyfin's session state so the
                    // client's now-playing UI updates on auto-advance. Without
                    // this, gapless-playback clients (Finer) keep showing the
                    // previous track even though new audio is streaming.
                    _ = NudgeSessionPlaybackStartAsync(ctx.HttpContext, aId, aEntry);

                    await ProxyAudioAsync(ctx.HttpContext, upstreamUrl).ConfigureAwait(false);
                    ctx.Result = new EmptyResult();
                    return;
                }
            }

            // (a) Single-item detail
            if (TryExtractSingleItemId(ctx, out var singleId) && _cache.TryGet(singleId, out var singleEntry) && singleEntry is not null)
            {
                if (singleEntry.Kind == "track") _registrar.RegisterStubTrack(singleId, singleEntry);
                _log.LogInformation("[mdiscover] single-item hit: {Id} kind={Kind}", singleId, singleEntry.Kind);
                ctx.Result = new ObjectResult(BuildDtoFromCache(singleId, singleEntry));
                return;
            }

            // (a-discovery) Single-item lookup for one of our synthetic
            // discovery playlists (Top 100 Global, etc.) — not in the regular
            // _cache, lives in DiscoveryPlaylistFeed.
            if (TryExtractSingleItemId(ctx, out var dpId))
            {
                var dp = _discoveryFeed.Get(dpId);
                if (dp is not null)
                {
                    _log.LogInformation("[mdiscover] discovery-playlist single-item hit: {Id} '{Name}'", dpId, dp.Name);
                    ctx.Result = new ObjectResult(BuildDiscoveryPlaylistDto(dp));
                    return;
                }
            }

            // (a-discovery-children) GET /Playlists/{stubId}/Items for one of
            // our synthetic playlists → return the curated track list.
            // Distinct from the user-playlist linkage path because the
            // children come from the DiscoveryPlaylistFeed snapshot, not
            // _playlistLinks.
            if (TryExtractDiscoveryPlaylistChildrenId(ctx, out var dpcId))
            {
                var dp = _discoveryFeed.Get(dpcId);
                if (dp is not null)
                {
                    var items = new List<BaseItemDto>();
                    foreach (var trackId in dp.TrackIds)
                    {
                        if (_cache.TryGet(trackId, out var tr) && tr is { Kind: "track" })
                        {
                            _registrar.RegisterStubTrack(trackId, tr);
                            items.Add(BuildDtoFromCache(trackId, tr));
                        }
                    }
                    _log.LogInformation("[mdiscover] discovery-playlist children: {Id} '{Name}' returning {N} tracks",
                        dpcId, dp.Name, items.Count);
                    ctx.Result = new ObjectResult(new QueryResult<BaseItemDto>(0, items.Count, items));
                    return;
                }
            }

            // (a2) Bulk lookup: /Items?ids=guid1,guid2,...
            // Finer / iOS Jellyfin app uses this to enrich its search/list results with
            // full metadata. If we don't satisfy it for our stub IDs, those items appear
            // "broken" in the client (no MediaSources, no real fields → no playback).
            if (TryExtractBulkIdsQuery(ctx.HttpContext, out var bulkIds) && bulkIds.Count > 0)
            {
                var hits = new List<BaseItemDto>();
                foreach (var bid in bulkIds)
                {
                    if (_cache.TryGet(bid, out var be) && be is not null)
                    {
                        if (be.Kind == "track") _registrar.RegisterStubTrack(bid, be);
                        hits.Add(BuildDtoFromCache(bid, be));
                    }
                }
                if (hits.Count > 0)
                {
                    _log.LogInformation("[mdiscover] bulk-ids hit: {Hits}/{Total} from cache", hits.Count, bulkIds.Count);
                    ctx.Result = new ObjectResult(new QueryResult<BaseItemDto>(0, hits.Count, hits));
                    return;
                }
            }

            // Read IncludeItemTypes once — multiple branches need to respect it.
            var includeTypes = ExtractIncludeItemTypes(ctx.HttpContext);
            var wantsAudio = includeTypes.Count == 0 || includeTypes.Contains("Audio");
            var wantsAlbum = includeTypes.Count == 0 || includeTypes.Contains("MusicAlbum");

            // (b) ParentId query → synthesize children list before Jellyfin's validator throws.
            if (TryExtractParentIdFromQuery(ctx.HttpContext, out var parentId) && _cache.TryGet(parentId, out var parentEntry) && parentEntry is not null)
            {
                _log.LogInformation("[mdiscover] parentId match: {Id} kind={Kind} wantsAudio={Audio} wantsAlbum={Album}", parentId, parentEntry.Kind, wantsAudio, wantsAlbum);
                if (parentEntry.Kind == "album" && !wantsAudio)
                {
                    // Album children that aren't audio (e.g. MusicVideo for related-content rows) → empty.
                    ctx.Result = new ObjectResult(new QueryResult<BaseItemDto>(0, 0, Array.Empty<BaseItemDto>()));
                    return;
                }
                if (parentEntry.Kind == "artist" && wantsAudio)
                {
                    // Artist's "Top Songs" row + shuffle button. Both come in as
                    // ParentId={artistGuid}&IncludeItemTypes=Audio. Return the
                    // artist's top tracks via Deezer.
                    var qr = await BuildArtistTopTracksAsync(parentId, parentEntry, ctx.HttpContext.RequestAborted).ConfigureAwait(false);
                    ctx.Result = new ObjectResult(qr);
                    return;
                }
                if (parentEntry.Kind == "artist" && !wantsAlbum)
                {
                    // Some other type asked for under the artist (e.g. MusicVideo) —
                    // don't fabricate; return empty so Jellyfin's validator doesn't 500.
                    ctx.Result = new ObjectResult(new QueryResult<BaseItemDto>(0, 0, Array.Empty<BaseItemDto>()));
                    return;
                }
                var qr2 = await BuildChildrenForStubAsync(parentId, parentEntry, ctx.HttpContext.RequestAborted).ConfigureAwait(false);
                ctx.Result = new ObjectResult(qr2);
                return;
            }

            // (c) AlbumIds=stubGuid → the music album-detail page queries tracks this way.
            var hasAlbumIds = TryExtractGuidListFromQuery(ctx.HttpContext, "albumIds", out var albumIds);
            _log.LogInformation("[mdiscover] albumIds extract: hasMatch={Has} count={N}", hasAlbumIds, albumIds.Count);
            if (hasAlbumIds)
            {
                foreach (var albumGuid in albumIds)
                {
                    var inCache = _cache.TryGet(albumGuid, out var albumEntry);
                    _log.LogInformation("[mdiscover] albumIds check: {Id} inCache={In} kind={Kind}", albumGuid, inCache, albumEntry?.Kind);
                    if (inCache && albumEntry is { Kind: "album" })
                    {
                        _log.LogInformation("[mdiscover] albumIds match: {Id} wantsAudio={Audio}", albumGuid, wantsAudio);
                        if (!wantsAudio)
                        {
                            ctx.Result = new ObjectResult(new QueryResult<BaseItemDto>(0, 0, Array.Empty<BaseItemDto>()));
                            return;
                        }
                        var qr = await BuildChildrenForStubAsync(albumGuid, albumEntry, ctx.HttpContext.RequestAborted).ConfigureAwait(false);
                        ctx.Result = new ObjectResult(qr);
                        return;
                    }
                }
            }

            // (d) ArtistIds / AlbumArtistIds / ContributingArtistIds=stubGuid →
            // the artist-detail page queries albums or audio. Different clients
            // use different param names for the same query — Finer/iOS uses
            // ArtistIds or AlbumArtistIds; Jellyfin web uses ContributingArtistIds.
            if (TryExtractGuidListFromQuery(ctx.HttpContext, "artistIds", out var artistIds)
                || TryExtractGuidListFromQuery(ctx.HttpContext, "albumArtistIds", out artistIds)
                || TryExtractGuidListFromQuery(ctx.HttpContext, "contributingArtistIds", out artistIds))
            {
                foreach (var artistGuid in artistIds)
                {
                    if (_cache.TryGet(artistGuid, out var artistEntry) && artistEntry is { Kind: "artist" })
                    {
                        if (wantsAlbum)
                        {
                            var qr = await BuildChildrenForStubAsync(artistGuid, artistEntry, ctx.HttpContext.RequestAborted).ConfigureAwait(false);
                            ctx.Result = new ObjectResult(qr);
                            return;
                        }
                        if (wantsAudio)
                        {
                            // Shuffle-artist / artist-page audio queries → return the
                            // artist's top tracks via Deezer. Same DTO shape as
                            // album children, but parented to the artist (no album).
                            var qr = await BuildArtistTopTracksAsync(artistGuid, artistEntry, ctx.HttpContext.RequestAborted).ConfigureAwait(false);
                            ctx.Result = new ObjectResult(qr);
                            return;
                        }
                        // Some other type asked for — send empty so Jellyfin's
                        // validator doesn't 500 on the unknown id.
                        ctx.Result = new ObjectResult(new QueryResult<BaseItemDto>(0, 0, Array.Empty<BaseItemDto>()));
                        return;
                    }
                }
            }

            // (d2) /Artists/{id}/Similar or /Items/{id}/Similar — the "More
            // Like This" row on the artist landing page. We map the stub
            // artist's Deezer ID to Deezer's /artist/{id}/related endpoint.
            if (IsSimilarRoute(ctx, out var simSeedId)
                && _cache.TryGet(simSeedId, out var simSeed) && simSeed is { Kind: "artist", DeezerArtistId: long simArtistId })
            {
                _log.LogInformation("[mdiscover] Similar request for stub artist {Id} (deezer={Dz})", simSeedId, simArtistId);
                var qr = await BuildSimilarArtistsAsync(simArtistId, simSeed, ctx.HttpContext.RequestAborted).ConfigureAwait(false);
                ctx.Result = new ObjectResult(qr);
                return;
            }

            // (e) InstantMix endpoints — radio-style "play similar songs" buttons.
            // Routes: /Artists/InstantMix?id=..., /Albums/{id}/InstantMix,
            //         /Items/{id}/InstantMix, /MusicGenres/InstantMix
            // Strategy: look up the seed item (artist or album) in our cache and
            // return its top tracks. For an album, we use the album's own tracks
            // plus the artist's top tracks; for an artist, just the top tracks.
            if (IsInstantMixRoute(ctx, out var mixSeedId)
                && _cache.TryGet(mixSeedId, out var mixSeed) && mixSeed is not null)
            {
                _log.LogInformation("[mdiscover] InstantMix on stub {Id} kind={Kind}", mixSeedId, mixSeed.Kind);
                var qr = await BuildInstantMixAsync(mixSeed, ctx.HttpContext.RequestAborted).ConfigureAwait(false);
                ctx.Result = new ObjectResult(qr);
                return;
            }
        }
        catch (Exception ex) { _log.LogDebug(ex, "Item-detail filter passthrough on error."); }

        await next().ConfigureAwait(false);
    }

    /// <summary>
    /// Result filter handles only one case today: GET /Playlists/{id}/Items.
    /// Jellyfin's playlist controller resolves linked children via SQL —
    /// our synthetic Audio items only live in the in-memory ItemRegistry,
    /// so they never appear in the response. We walk the playlist's
    /// LinkedChildren ourselves and inject any stubs we recognize.
    /// </summary>
    public async Task OnResultExecutionAsync(ResultExecutingContext ctx, ResultExecutionDelegate next)
    {
        try
        {
            var path = ctx.HttpContext.Request.Path.Value ?? string.Empty;
            if (HttpMethods.IsGet(ctx.HttpContext.Request.Method)
                && path.IndexOf("/Playlists/", StringComparison.OrdinalIgnoreCase) >= 0
                && path.EndsWith("/Items", StringComparison.OrdinalIgnoreCase))
            {
                _log.LogInformation("[mdiscover] playlist-items result filter fire: {Path}", path);
                AugmentPlaylistItems(ctx, path);
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "[mdiscover] playlist-items result filter error"); }
        await next().ConfigureAwait(false);
    }

    private void AugmentPlaylistItems(ResultExecutingContext ctx, string path)
    {
        if (ctx.Result is not ObjectResult or || or.Value is not QueryResult<BaseItemDto> qr) return;
        if (!TryGuidAfterSegment(path, "/Playlists/", out var playlistId)) return;

        // Walk our own per-playlist stub links — survives restart, doesn't
        // depend on Jellyfin's path-based resolution (which can't find our
        // synthetic items).
        var stubLinks = _playlistLinks.GetLinks(playlistId);
        if (stubLinks.Count == 0) return;

        var existingIds = qr.Items.Select(i => i.Id).ToHashSet();
        var addItems = new List<BaseItemDto>();
        foreach (var stubId in stubLinks)
        {
            if (existingIds.Contains(stubId)) continue;
            if (!_cache.TryGet(stubId, out var entry) || entry is not { Kind: "track" }) continue;
            // Lazy register: makes the synthetic Audio visible to Jellyfin's
            // session-state lookups (so playback session reporting works).
            _registrar.RegisterStubTrack(stubId, entry);
            addItems.Add(BuildDtoFromCache(stubId, entry));
            existingIds.Add(stubId);
        }

        if (addItems.Count == 0) return;

        var combined = qr.Items.Concat(addItems).ToArray();
        qr.Items = combined;
        qr.TotalRecordCount = combined.Length;
        _log.LogInformation("[mdiscover] playlist {PlaylistId} GET: injected {N} stub tracks (have {Total} links)",
            playlistId, addItems.Count, stubLinks.Count);
    }

    private async Task<QueryResult<BaseItemDto>> BuildChildrenForStubAsync(Guid parentId, DiscoveryItemCache.Entry parent, CancellationToken ct)
    {
        var items = new List<BaseItemDto>();
        _log.LogInformation("[mdiscover] BuildChildren start: parent={Id} kind={Kind} dzAlbumId={Album}", parentId, parent.Kind, parent.DeezerAlbumId);

        if (parent.Kind == "album" && parent.DeezerAlbumId is long dzAlbumId)
        {
            var dzTracks = await _deezer.GetAlbumTracksAsync(dzAlbumId, ct).ConfigureAwait(false);
            _log.LogInformation("[mdiscover] BuildChildren deezer returned: count={N}", dzTracks?.Data?.Count ?? -1);
            if (dzTracks?.Data is not null)
            {
                foreach (var t in dzTracks.Data)
                {
                    var id = DiscoveryManager.StubGuid("dz-track", t.Id.ToString());
                    var trackEntry = new DiscoveryItemCache.Entry
                    {
                        Kind = "track",
                        Artist = t.Artist?.Name ?? parent.Artist,
                        AlbumName = parent.AlbumName,
                        TrackTitle = t.Title,
                        DeezerTrackId = t.Id,
                        DeezerAlbumId = dzAlbumId,
                        DeezerArtistId = t.Artist?.Id ?? parent.DeezerArtistId,
                        PrimaryImageUrl = parent.PrimaryImageUrl,
                        DurationSeconds = t.Duration,
                    };
                    _cache.Put(id, trackEntry);
                    _registrar.RegisterStubTrack(id, trackEntry);
                    items.Add(MusicSearchActionFilter.BuildTrackDto(id, t, parentId));
                }
            }
        }

        // Artist landing page → list the artist's albums via Deezer.
        // Artist landing pages typically query for MusicAlbum children; we
        // synthesize stub albums and cache them so subsequent clicks resolve.
        else if (parent.Kind == "artist" && parent.DeezerArtistId is long dzArtistId)
        {
            // Pull a generous page; Deezer caps at 100 by default and most artists
            // have far fewer. Quality filter strips karaoke/tribute compilations.
            var dzAlbums = await _deezer.GetArtistAlbumsAsync(dzArtistId, 100, ct).ConfigureAwait(false);
            _log.LogInformation("[mdiscover] BuildChildren (artist) deezer returned: count={N}", dzAlbums?.Data?.Count ?? -1);
            if (dzAlbums?.Data is not null)
            {
                // Stamp the parent artist onto each album so the album DTO can
                // wire AlbumArtist correctly even when Deezer omits it on the
                // /artist/{id}/albums response.
                foreach (var al in dzAlbums.Data)
                {
                    if (al.Artist is null && parent.Artist is { } pa)
                    {
                        al.Artist = new DeezerClient.DzArtist { Id = dzArtistId, Name = pa };
                    }
                }
                // Apply the same quality filter we use in search (skip karaoke / tribute /
                // various artists). MusicSearchActionFilter exposes IsQualityAlbum statically...
                // ...except it's currently private. We replicate the bare minimum here:
                // reject record_type "compile" and bad-keyword matches in the title.
                bool keep(DeezerClient.DzAlbum al)
                {
                    if (al.RecordType is { } rt && !string.Equals(rt, "album", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(rt, "ep", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(rt, "single", StringComparison.OrdinalIgnoreCase))
                        return false;
                    var t = (al.Title ?? string.Empty).ToLowerInvariant();
                    foreach (var b in new[] { "karaoke", "tribute", "covers of", "in the style of",
                                              "made famous by", "originally performed",
                                              "instrumental versions", "music box", "soundalike" })
                        if (t.Contains(b)) return false;
                    return true;
                }

                foreach (var al in dzAlbums.Data.Where(keep))
                {
                    var id = DiscoveryManager.StubGuid("dz-album", al.Id.ToString());
                    _cache.Put(id, new DiscoveryItemCache.Entry
                    {
                        Kind = "album",
                        Artist = al.Artist?.Name ?? parent.Artist,
                        AlbumName = al.Title,
                        DeezerAlbumId = al.Id,
                        DeezerArtistId = al.Artist?.Id ?? dzArtistId,
                        PrimaryImageUrl = al.CoverXl ?? al.CoverBig,
                    });
                    items.Add(MusicSearchActionFilter.BuildAlbumDto(id, al));
                }
            }
        }

        _log.LogInformation("[mdiscover] BuildChildren returning: items.Count={N}", items.Count);
        return new QueryResult<BaseItemDto>(0, items.Count, items);
    }

    /// <summary>
    /// Build the artist's top tracks list. Used for the "Top Songs" row on
    /// the artist page, the Shuffle button, and as a seed for InstantMix.
    /// </summary>
    private async Task<QueryResult<BaseItemDto>> BuildArtistTopTracksAsync(Guid artistStubId, DiscoveryItemCache.Entry artist, CancellationToken ct)
    {
        var items = new List<BaseItemDto>();
        if (artist.DeezerArtistId is not long dzArtistId)
        {
            _log.LogInformation("[mdiscover] BuildArtistTopTracks: no DeezerArtistId on cache entry");
            return new QueryResult<BaseItemDto>(0, 0, items);
        }

        var top = await _deezer.GetArtistTopTracksAsync(dzArtistId, 50, ct).ConfigureAwait(false);
        _log.LogInformation("[mdiscover] BuildArtistTopTracks: deezer returned {N} tracks", top?.Data?.Count ?? -1);
        if (top?.Data is null) return new QueryResult<BaseItemDto>(0, 0, items);

        foreach (var t in top.Data)
        {
            var trackId = DiscoveryManager.StubGuid("dz-track", t.Id.ToString());

            // Cache the embedded album too so click-through to the album works.
            // The /artist/{id}/top response includes the parent album for each
            // track, which is the same shape we get from search.
            if (t.Album is { Id: > 0 } al)
            {
                var albumStubId = DiscoveryManager.StubGuid("dz-album", al.Id.ToString());
                if (!_cache.TryGet(albumStubId, out var existing) || existing is null)
                {
                    _cache.Put(albumStubId, new DiscoveryItemCache.Entry
                    {
                        Kind = "album",
                        Artist = t.Artist?.Name ?? artist.Artist,
                        AlbumName = al.Title,
                        DeezerAlbumId = al.Id,
                        DeezerArtistId = t.Artist?.Id ?? dzArtistId,
                        PrimaryImageUrl = al.CoverXl ?? al.CoverBig,
                    });
                }
            }

            var trackEntry = new DiscoveryItemCache.Entry
            {
                Kind = "track",
                Artist = t.Artist?.Name ?? artist.Artist,
                AlbumName = t.Album?.Title,
                TrackTitle = t.Title,
                DeezerTrackId = t.Id,
                DeezerAlbumId = t.Album?.Id,
                DeezerArtistId = t.Artist?.Id ?? dzArtistId,
                PrimaryImageUrl = t.Album?.CoverBig,
                DurationSeconds = t.Duration,
            };
            _cache.Put(trackId, trackEntry);
            _registrar.RegisterStubTrack(trackId, trackEntry);

            // Pass the parent album guid (if any) so AlbumId/ParentId on the
            // track DTO line up with the cached album stub.
            Guid? parentAlbumStub = t.Album is { Id: > 0 } al2
                ? DiscoveryManager.StubGuid("dz-album", al2.Id.ToString())
                : (Guid?)null;
            items.Add(MusicSearchActionFilter.BuildTrackDto(trackId, t, parentAlbumStub));
        }

        return new QueryResult<BaseItemDto>(0, items.Count, items);
    }

    /// <summary>
    /// Match the various "Similar/More Like This" route shapes:
    ///   GET /Artists/{guid}/Similar
    ///   GET /Items/{guid}/Similar
    /// </summary>
    private static bool IsSimilarRoute(ActionExecutingContext ctx, out Guid seedId)
    {
        seedId = default;
        if (!HttpMethods.IsGet(ctx.HttpContext.Request.Method)) return false;
        var path = ctx.HttpContext.Request.Path.Value ?? string.Empty;
        if (path.IndexOf("/Similar", StringComparison.OrdinalIgnoreCase) < 0) return false;
        if (path.IndexOf("/Artists/", StringComparison.OrdinalIgnoreCase) < 0
            && path.IndexOf("/Items/", StringComparison.OrdinalIgnoreCase) < 0)
            return false;
        return TryGuidBeforeSegment(path, "/Similar", out seedId);
    }

    /// <summary>
    /// Build a "More Like This" response for an artist seed using Deezer's
    /// related-artists list.
    /// </summary>
    private async Task<QueryResult<BaseItemDto>> BuildSimilarArtistsAsync(long dzArtistId, DiscoveryItemCache.Entry seed, CancellationToken ct)
    {
        var related = await _deezer.GetArtistRelatedAsync(dzArtistId, 20, ct).ConfigureAwait(false);
        if (related?.Data is null || related.Data.Count == 0)
        {
            return new QueryResult<BaseItemDto>(0, 0, Array.Empty<BaseItemDto>());
        }

        var items = new List<BaseItemDto>();
        foreach (var a in related.Data)
        {
            if (a.Id == 0 || string.IsNullOrEmpty(a.Name)) continue;
            var artistStubId = DiscoveryManager.StubGuid("dz-artist", a.Id.ToString());
            // Cache the related artist so click-through works (and survives
            // the disk-persisted cache layer).
            if (!_cache.TryGet(artistStubId, out var existing) || existing is null)
            {
                _cache.Put(artistStubId, new DiscoveryItemCache.Entry
                {
                    Kind = "artist",
                    Artist = a.Name,
                    DeezerArtistId = a.Id,
                    PrimaryImageUrl = a.PictureXl ?? a.PictureBig,
                });
            }
            items.Add(MusicSearchActionFilter.BuildArtistDto(artistStubId, a.Name));
        }

        _log.LogInformation("[mdiscover] BuildSimilarArtists: returning {N} related artists for seed deezer={Dz}",
            items.Count, dzArtistId);
        return new QueryResult<BaseItemDto>(0, items.Count, items);
    }

    /// <summary>
    /// Build an InstantMix track list for an artist or album seed. For now we
    /// just return the artist's top tracks (the simplest reasonable interpretation
    /// of "play me a mix based on this"). Could later expand to genre-based
    /// tracks via Deezer's `/genre/{id}/radios` endpoint.
    /// </summary>
    private async Task<QueryResult<BaseItemDto>> BuildInstantMixAsync(DiscoveryItemCache.Entry seed, CancellationToken ct)
    {
        // Resolve the artist behind the seed.
        long? dzArtistId = seed.Kind switch
        {
            "artist" => seed.DeezerArtistId,
            "album" or "track" => seed.DeezerArtistId,
            _ => null,
        };
        if (dzArtistId is null)
        {
            _log.LogInformation("[mdiscover] InstantMix seed has no artist info; returning empty");
            return new QueryResult<BaseItemDto>(0, 0, Array.Empty<BaseItemDto>());
        }

        var stubArtistId = DiscoveryManager.StubGuid("dz-artist", dzArtistId.Value.ToString());
        // Synthesize a transient artist entry so BuildArtistTopTracksAsync can use it.
        if (!_cache.TryGet(stubArtistId, out var artistEntry) || artistEntry is null)
        {
            artistEntry = new DiscoveryItemCache.Entry
            {
                Kind = "artist",
                Artist = seed.Artist,
                DeezerArtistId = dzArtistId.Value,
            };
            _cache.Put(stubArtistId, artistEntry);
        }

        return await BuildArtistTopTracksAsync(stubArtistId, artistEntry, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Detect InstantMix endpoint variants used by Jellyfin clients:
    ///   GET /Artists/InstantMix?id={artistId}
    ///   GET /Albums/{id}/InstantMix
    ///   GET /Items/{id}/InstantMix
    ///   GET /MusicGenres/InstantMix?id=...
    /// Returns the seed item's GUID — the thing we should mix from.
    /// </summary>
    private static bool IsInstantMixRoute(ActionExecutingContext ctx, out Guid seedId)
    {
        seedId = default;
        var path = ctx.HttpContext.Request.Path.Value ?? string.Empty;
        if (path.IndexOf("InstantMix", StringComparison.OrdinalIgnoreCase) < 0) return false;

        // /Artists/InstantMix?id=... — seed in query string
        if (path.IndexOf("/Artists/InstantMix", StringComparison.OrdinalIgnoreCase) >= 0
            || path.IndexOf("/MusicGenres/InstantMix", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            foreach (var k in new[] { "id", "Id", "ItemId", "itemId" })
            {
                if (ctx.HttpContext.Request.Query.TryGetValue(k, out var v)
                    && Guid.TryParse(v.ToString(), out seedId))
                    return true;
            }
            return false;
        }

        // /Albums/{guid}/InstantMix or /Items/{guid}/InstantMix — seed in path.
        if (TryGuidBeforeSegment(path, "/InstantMix", out seedId)) return true;

        return false;
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------
    private static bool TryExtractSingleItemId(ActionExecutingContext ctx, out Guid id)
    {
        id = default;
        var route = ctx.RouteData.Values;
        var controller = route.TryGetValue("controller", out var c) ? c?.ToString() : null;
        if (!string.Equals(controller, "Items", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(controller, "UserLibrary", StringComparison.OrdinalIgnoreCase))
            return false;

        // We only want to short-circuit single-item lookups, not list endpoints.
        // The list endpoints use action names with "Items" plural in their template;
        // single-item endpoints like GetItem(itemId) include itemId in the route.
        if (!route.TryGetValue("itemId", out var rv) && !route.TryGetValue("ItemId", out rv))
            return false;
        if (rv is null) return false;
        if (!Guid.TryParse(rv.ToString(), out id)) return false;

        // Reject sub-action paths — `/Items/{id}/Intros`, `/Items/{id}/Similar`,
        // `/Items/{id}/SpecialFeatures`, `/Items/{id}/ThemeMedia`, etc. are
        // LIST endpoints whose itemId route value is the parent. If we
        // returned the parent's DTO here we'd give the player back garbage
        // for what it expects to be an empty list — and that breaks
        // playback initiation for music videos. Only accept the bare
        // single-item path: ends with the guid (no further segment).
        var path = ctx.HttpContext.Request.Path.Value ?? string.Empty;
        var idStr = id.ToString("N");
        var idDashed = id.ToString("D");
        // The path can have either dashed or undashed guid form.
        bool endsWithGuid =
            path.EndsWith("/" + idStr, StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/" + idDashed, StringComparison.OrdinalIgnoreCase);
        if (!endsWithGuid) { id = default; return false; }
        return true;
    }

    private static bool TryExtractParentIdFromQuery(HttpContext http, out Guid parentId)
    {
        parentId = default;
        foreach (var key in new[] { "parentId", "ParentId" })
        {
            if (http.Request.Query.TryGetValue(key, out var qs))
            {
                if (Guid.TryParse(qs.ToString(), out parentId)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Look up a real Jellyfin library item (not one of our stubs) and, if it
    /// resolves to an album/track with usable artist+title metadata, fire a
    /// Lidarr add. This is the path that handles the case where a user has
    /// SOME tracks of an album in their library — Jellyfin builds a partial
    /// album record, the user favorites THAT (because Finer dedups against
    /// our stub), and we need to still tell Lidarr "go fill in the rest".
    /// </summary>
    /// <returns>true if we recognized the item and queued a Lidarr add.</returns>
    private bool TryFireLidarrFromLibraryItem(Guid itemId)
    {
        try
        {
            var item = _libraryManager.GetItemById(itemId);
            if (item is null)
            {
                return false;
            }

            string? artist = null;
            string? album = null;

            // Item could be a MusicAlbum or an Audio (track). For a track we
            // climb to its parent album to get the album-grained info Lidarr
            // wants.
            if (item is MediaBrowser.Controller.Entities.Audio.MusicAlbum musicAlbum)
            {
                album = musicAlbum.Name;
                artist = musicAlbum.AlbumArtist
                    ?? musicAlbum.Artists?.FirstOrDefault()
                    ?? musicAlbum.GetParent()?.Name;
            }
            else if (item is MediaBrowser.Controller.Entities.Audio.Audio track)
            {
                album = track.Album;
                artist = track.AlbumArtists?.FirstOrDefault()
                    ?? track.Artists?.FirstOrDefault();
            }
            else
            {
                _log.LogInformation("[mdiscover] favorite on real library item but it's not Album/Audio: {Type} (id={Id})",
                    item.GetType().Name, itemId);
                return false;
            }

            if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(album))
            {
                _log.LogInformation("[mdiscover] favorite on real library item, but missing artist/album metadata: artist='{Artist}' album='{Album}' (id={Id})",
                    artist, album, itemId);
                return false;
            }

            _log.LogInformation("[mdiscover] favorite on real library item → asking Lidarr for: {Artist} - {Album} (id={Id})",
                artist, album, itemId);

            // Same cooldown as stub favorites — keyed by a synthetic long
            // hash of the Jellyfin GUID since real items don't have a Deezer
            // album id. We just want to dedupe rapid taps; the hash space is
            // wide enough that collisions are practically impossible.
            var key = unchecked((long)itemId.GetHashCode()) ^ unchecked((long)artist.GetHashCode() << 32);
            var now = DateTime.UtcNow;
            if (_lidarrLastFiredAt.TryGetValue(key, out var last) && (now - last) < _lidarrFireCooldown)
            {
                _log.LogInformation("[mdiscover] favorite Lidarr skip (cooldown, library item): {Artist} - {Album}", artist, album);
                return true; // we recognized it; just rate-limited
            }
            _lidarrLastFiredAt[key] = now;

            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await _lidarr.AddAlbumByTextAsync(artist, album, default).ConfigureAwait(false);
                    _log.LogInformation("[mdiscover] Lidarr add result (library item): success={Success} msg={Msg}", result.Success, result.Message);
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[mdiscover] background Lidarr add (library item) threw");
                }
            });
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[mdiscover] TryFireLidarrFromLibraryItem failed for {Id}", itemId);
            return false;
        }
    }

    /// <summary>
    /// Triggers a background Lidarr "add album" call. Accepts an album-kind entry
    /// directly (favoriting an album → add that album) or a track-kind entry
    /// (favoriting a track → add the track's parent album, since Lidarr is
    /// album-grained).
    ///
    /// The Lidarr-side flow is idempotent (already-added → flip monitored;
    /// monitor flip is a no-op if already true), so re-firing is safe. We
    /// just want to avoid mashing the heart icon spamming Lidarr — hence the
    /// 15-second per-album cooldown rather than permanent dedup.
    /// </summary>
    private void FireLidarrAddIfFavorited(DiscoveryItemCache.Entry entry)
    {
        // Track-level favorites: download just the one song via slskd
        // (Soulseek). Lidarr is fundamentally an album-level tool — it
        // would download the full album for a single starred track. For
        // playlist/individual-discovery favorites that's overkill, so we
        // route through slskd which can grab just the file.
        if (entry.Kind == "track")
        {
            FireSlskdTrackDownload(entry);
            return;
        }

        // Album/artist favorites still go to Lidarr — that's the right
        // grain for those.
        long? dzAlbumId = entry.Kind switch
        {
            "album" => entry.DeezerAlbumId,
            _ => null,
        };
        var rawArtist = entry.Artist;
        var rawAlbum = entry.AlbumName;

        if (dzAlbumId is null || string.IsNullOrEmpty(rawArtist) || string.IsNullOrEmpty(rawAlbum))
        {
            _log.LogInformation("[mdiscover] favorite skip Lidarr (kind={Kind}, missing data: dzAlbumId={DzId} artist={Artist} album={Album})",
                entry.Kind, dzAlbumId, rawArtist, rawAlbum);
            return;
        }

        // Same normalization as the slskd path so Lidarr's text search
        // (which then drives Soularr → slskd queries) gets a clean
        // "ArtistName AlbumName" string.
        var artist = DiscoveryPlaylistFeed.NormalizeArtistForSearch(rawArtist);
        var albumName = DiscoveryPlaylistFeed.NormalizeAlbumForSearch(rawAlbum);

        var now = DateTime.UtcNow;
        if (_lidarrLastFiredAt.TryGetValue(dzAlbumId.Value, out var last) && (now - last) < _lidarrFireCooldown)
        {
            _log.LogInformation("[mdiscover] favorite Lidarr skip (cooldown): {Artist} - {Album} fired {SecAgo}s ago",
                artist, albumName, (int)(now - last).TotalSeconds);
            return;
        }
        _lidarrLastFiredAt[dzAlbumId.Value] = now;

        _ = Task.Run(async () =>
        {
            try
            {
                _log.LogInformation("[mdiscover] favorite → Lidarr add: '{Artist}' '{Album}' (raw: '{RawA}' '{RawAl}')",
                    artist, albumName, rawArtist, rawAlbum);
                var result = await _lidarr.AddAlbumByTextAsync(artist, albumName, default).ConfigureAwait(false);
                _log.LogInformation("[mdiscover] Lidarr add result: success={Success} msg={Msg}", result.Success, result.Message);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[mdiscover] background Lidarr add threw");
            }
        });
    }

    // Per-(artist+title) cooldown for slskd track downloads — keeps
    // double-tap heart from queueing the same file twice in 15s.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _slskdLastFiredAt = new();

    private void FireSlskdTrackDownload(DiscoveryItemCache.Entry entry)
    {
        var rawArtist = entry.Artist;
        var rawTitle = entry.TrackTitle;
        if (string.IsNullOrEmpty(rawArtist) || string.IsNullOrEmpty(rawTitle))
        {
            _log.LogInformation("[mdiscover] favorite skip slskd (track missing artist/title)");
            return;
        }

        // Strip YT-style noise from the title ("(Official Video)", "(feat. X)",
        // "(Lyric Video)", etc.) and reduce the artist to the lead so slskd's
        // search ranks the actual recording first instead of fancams or
        // YT-only re-uploads. Same normalizers DiscoveryPlaylistFeed uses
        // when matching tracks against Deezer/iTunes.
        var artist = DiscoveryPlaylistFeed.NormalizeArtistForSearch(rawArtist);
        var title = DiscoveryPlaylistFeed.NormalizeTitleForSearch(rawTitle);

        var key = $"{artist}|{title}".ToLowerInvariant();
        var now = DateTime.UtcNow;
        if (_slskdLastFiredAt.TryGetValue(key, out var last) && (now - last) < _lidarrFireCooldown)
        {
            _log.LogInformation("[mdiscover] favorite slskd skip (cooldown): {Artist} - {Title}", artist, title);
            return;
        }
        _slskdLastFiredAt[key] = now;

        _ = Task.Run(async () =>
        {
            try
            {
                _log.LogInformation("[mdiscover] favorite → slskd track download: '{Artist}' '{Title}' (raw: '{RawA}' '{RawT}')",
                    artist, title, rawArtist, rawTitle);
                var handle = await _slskd.SearchAndDownloadAsync(artist, title, default).ConfigureAwait(false);
                if (handle is null)
                {
                    _log.LogInformation("[mdiscover] slskd no match for: {Artist} - {Title}", artist, title);
                    return;
                }
                _log.LogInformation("[mdiscover] slskd queued: {User} {File}", handle.Username, handle.RemoteFile);
                // Hand off to the post-download organizer: it polls slskd
                // until completion, reads tags, moves the file into the
                // music library, and (eventually) triggers a Jellyfin
                // scan. Fully fire-and-forget — failures are logged but
                // don't bubble up.
                var organized = await _slskdFinisher.WatchAndOrganizeAsync(handle, artist, title, default)
                    .ConfigureAwait(false);
                if (organized is not null)
                    _log.LogInformation("[mdiscover] slskd track auto-organized to {Path}", organized);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[mdiscover] background slskd download threw");
            }
        });
    }

    /// <summary>
    /// Detects every flavour of "user expressed interest in this item" the
    /// Jellyfin clients use, on both POST (favorite) and DELETE (unfavorite).
    ///
    /// Why fire Lidarr on unfavorite too? Because Finer/iOS clients can get
    /// stuck thinking a stub album is already favorited (from a prior session
    /// where the server saved IsFavorite but Lidarr never ran). Tapping the
    /// heart then sends DELETE. From the user's POV they're going "favorite
    /// this!" — they don't see a difference. Firing Lidarr on either method
    /// matches user intent. Lidarr is idempotent (already-added → flip
    /// monitored, monitor flip is a no-op if already true), so it's safe.
    ///
    /// Routes seen:
    ///   /Users/{userId}/FavoriteItems/{itemId}     (legacy)
    ///   /UserFavoriteItems/{itemId}                (modern)
    ///   /Users/{userId}/Items/{itemId}/Favorite    (very old)
    /// </summary>
    private bool IsFavoriteRoute(ActionExecutingContext ctx, out Guid itemId)
    {
        itemId = default;
        var path = ctx.HttpContext.Request.Path.Value ?? string.Empty;

        // Diagnostic: any request whose path mentions "favorite" — logged
        // unconditionally so we can spot exotic methods (PUT, PATCH, weird
        // PROPPATCH, etc) that some clients use for heart toggles.
        if (path.IndexOf("favorite", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            _log.LogInformation("[mdiscover] favorite-ish {Method} seen: {Path}",
                ctx.HttpContext.Request.Method, path);
        }

        if (TryGuidAfterSegment(path, "/FavoriteItems/", out itemId)) return true;
        if (TryGuidAfterSegment(path, "/UserFavoriteItems/", out itemId)) return true;
        if (TryGuidBeforeSegment(path, "/Favorite", out itemId)) return true;

        return false;
    }

    /// <summary>
    /// Detects the body-based "update user data" endpoints used by Finer / iOS:
    ///   POST /UserItems/{itemId}/UserData
    ///   POST /Users/{userId}/Items/{itemId}/UserData
    /// The HTTP path doesn't say "favorite" — that's in the JSON body. We peek
    /// at the action's bound arguments via reflection, looking for any object
    /// with an `IsFavorite` property set to true.
    /// </summary>
    private bool IsUserDataUpdateRoute(ActionExecutingContext ctx, out Guid itemId, out bool isFavorite)
    {
        itemId = default;
        isFavorite = false;
        if (!HttpMethods.IsPost(ctx.HttpContext.Request.Method)) return false;
        var path = ctx.HttpContext.Request.Path.Value ?? string.Empty;
        if (path.IndexOf("/UserData", StringComparison.OrdinalIgnoreCase) < 0) return false;

        // Diagnostic — log every POST that hits the UserData route, regardless
        // of whether we resolve an item from it. Helps spot path drift across
        // clients (Finer vs iOS Jellyfin vs web).
        _log.LogInformation("[mdiscover] UserData POST seen: {Path}", path);

        // Pull the item id (the segment immediately before /UserData).
        if (!TryGuidBeforeSegment(path, "/UserData", out itemId)) return false;

        // Walk the bound action args and look for an object exposing IsFavorite.
        // Reflection is fine here — it's one POST per heart tap, not a hot loop.
        foreach (var arg in ctx.ActionArguments.Values)
        {
            if (arg is null) continue;
            var prop = arg.GetType().GetProperty("IsFavorite",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (prop is null) continue;
            var raw = prop.GetValue(arg);
            // Property might be `bool` or `bool?`. When reflection unboxes a
            // bool? with a value, you get a boxed bool — `is bool b` covers both.
            // A null bool? becomes null and falls through, which is correct.
            if (raw is bool b) { isFavorite = b; return true; }
        }
        // Couldn't find IsFavorite — still return true so we short-circuit
        // (item exists in our cache; we just don't know whether it's favorite/unfavorite).
        // The caller treats `isFavorite=false` as "no Lidarr fire, just return DTO".
        return true;
    }

    private static bool TryGuidAfterSegment(string path, string segment, out Guid id)
    {
        id = default;
        var i = path.IndexOf(segment, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return false;
        var rest = path.Substring(i + segment.Length).Trim('/');
        var slash = rest.IndexOf('/');
        var idStr = slash > 0 ? rest.Substring(0, slash) : rest;
        return Guid.TryParse(idStr, out id);
    }

    private static bool TryGuidBeforeSegment(string path, string segment, out Guid id)
    {
        id = default;
        var i = path.IndexOf(segment, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return false;
        // Walk back from segment start, find the previous '/', take the GUID between.
        var slice = path.Substring(0, i);
        var lastSlash = slice.LastIndexOf('/');
        if (lastSlash < 0 || lastSlash + 1 >= slice.Length) return false;
        var idStr = slice.Substring(lastSlash + 1);
        return Guid.TryParse(idStr, out id);
    }

    private static bool IsPlaybackInfoRoute(ActionExecutingContext ctx)
    {
        var path = ctx.HttpContext.Request.Path.Value ?? string.Empty;
        return path.IndexOf("/PlaybackInfo", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Intercept POST /Playlists/{id}/Items for stub track adds. Stores the
    /// linkage in our own PlaylistStubLinks file (disk-persisted) rather than
    /// Jellyfin's playlist.LinkedChildren — Jellyfin's storage uses path-based
    /// resolution that can't find our synthetic items on restart.
    /// </summary>
    private Task<bool> TryHandleAddItemsToPlaylistAsync(ActionExecutingContext ctx)
    {
        var path = ctx.HttpContext.Request.Path.Value ?? string.Empty;
        if (!HttpMethods.IsPost(ctx.HttpContext.Request.Method)) return Task.FromResult(false);
        if (path.IndexOf("/Playlists/", StringComparison.OrdinalIgnoreCase) < 0) return Task.FromResult(false);
        if (!path.EndsWith("/Items", StringComparison.OrdinalIgnoreCase)) return Task.FromResult(false);

        if (!TryGuidAfterSegment(path, "/Playlists/", out var playlistId)) return Task.FromResult(false);

        var ids = new List<Guid>();
        foreach (var k in new[] { "ids", "Ids" })
        {
            if (ctx.HttpContext.Request.Query.TryGetValue(k, out var qs))
            {
                foreach (var s in qs.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (Guid.TryParse(s, out var g)) ids.Add(g);
            }
        }
        if (ids.Count == 0) return Task.FromResult(false);

        // Split into stubs (handled by us) and reals (let Jellyfin handle).
        var stubIds = ids.Where(g => _cache.TryGet(g, out var e) && e is { Kind: "track" }).ToList();
        if (stubIds.Count == 0) return Task.FromResult(false);

        // For now, if mixed (stubs + reals), we still fully handle. Reals
        // would need to be re-routed to Jellyfin's controller manually.
        // Single-id adds (the common case in client UIs) work fine.
        foreach (var sid in stubIds)
        {
            if (_cache.TryGet(sid, out var entry) && entry is { Kind: "track" })
            {
                _registrar.RegisterStubTrack(sid, entry);
            }
            _playlistLinks.AddLink(playlistId, sid);
        }
        _log.LogInformation("[mdiscover] manual playlist add: linked {N} stubs to playlist {Id}",
            stubIds.Count, playlistId);

        ctx.Result = new NoContentResult();
        return Task.FromResult(true);
    }

    /// <summary>
    /// Intercept DELETE /Playlists/{id}/Items?entryIds=... for stub-track
    /// removes. Drops our linkage entry; Jellyfin's controller still runs
    /// for any real items.
    /// </summary>
    private bool TryHandleRemoveItemsFromPlaylist(ActionExecutingContext ctx)
    {
        var path = ctx.HttpContext.Request.Path.Value ?? string.Empty;
        if (!HttpMethods.IsDelete(ctx.HttpContext.Request.Method)) return false;
        if (path.IndexOf("/Playlists/", StringComparison.OrdinalIgnoreCase) < 0) return false;
        if (!path.EndsWith("/Items", StringComparison.OrdinalIgnoreCase)) return false;

        if (!TryGuidAfterSegment(path, "/Playlists/", out var playlistId)) return false;

        // Jellyfin's DELETE uses entryIds (LinkedChild.Id, not ItemId) —
        // but for our injected stubs the entry id IS the stub guid, since
        // we never add a Jellyfin-side entry. So treat them as same.
        var ids = new List<Guid>();
        foreach (var k in new[] { "entryIds", "EntryIds", "ids", "Ids" })
        {
            if (ctx.HttpContext.Request.Query.TryGetValue(k, out var qs))
            {
                foreach (var s in qs.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (Guid.TryParse(s, out var g)) ids.Add(g);
            }
        }
        if (ids.Count == 0) return false;

        int removed = 0;
        foreach (var id in ids)
        {
            if (_playlistLinks.RemoveLink(playlistId, id)) removed++;
        }
        if (removed > 0)
        {
            _log.LogInformation("[mdiscover] playlist remove: dropped {N} stub links from playlist {Id}", removed, playlistId);
        }
        // We DON'T short-circuit — let Jellyfin's controller continue in case
        // other ids in the request are real items that need its handling.
        return false;
    }

    /// <summary>
    /// For POST /Playlists or POST /Playlists/{id}/Items, walk every item id
    /// referenced by the request and pre-register any that match a stub in
    /// our cache. Without this, Jellyfin's PlaylistManager throws "No item
    /// exists with the supplied Id" before our DI-based registration paths
    /// have any chance to fire.
    ///
    /// Ids come from three places depending on the route:
    ///   1. Query string ?ids=g1,g2,...                 (POST /Playlists/{id}/Items)
    ///   2. Bound CreatePlaylistDto.Ids on the body     (POST /Playlists)
    ///   3. Bound IReadOnlyList&lt;Guid&gt; ids parameter  (older shape)
    /// </summary>
    private void EnsureStubsRegisteredForPlaylistRequest(ActionExecutingContext ctx)
    {
        var path = ctx.HttpContext.Request.Path.Value ?? string.Empty;
        if (path.IndexOf("/Playlists", StringComparison.OrdinalIgnoreCase) < 0) return;
        if (!HttpMethods.IsPost(ctx.HttpContext.Request.Method)) return;

        var idsToRegister = new List<Guid>();

        // Source 1: query string ?ids=...
        foreach (var k in new[] { "ids", "Ids" })
        {
            if (ctx.HttpContext.Request.Query.TryGetValue(k, out var qs))
            {
                foreach (var s in qs.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (Guid.TryParse(s, out var g)) idsToRegister.Add(g);
            }
        }

        // Source 2 & 3: walk bound action arguments via reflection. Look for
        // any property/field named "Ids" or "ItemIds" containing GUIDs.
        foreach (var arg in ctx.ActionArguments.Values)
        {
            if (arg is null) continue;
            // Direct GUID list arg (e.g. parameter `IReadOnlyList<Guid> ids`)
            if (arg is System.Collections.IEnumerable enumerable && arg is not string)
            {
                foreach (var item in enumerable)
                {
                    if (item is Guid g && g != Guid.Empty) idsToRegister.Add(g);
                }
            }
            // Object with Ids/ItemIds property
            var t = arg.GetType();
            foreach (var name in new[] { "Ids", "ItemIds" })
            {
                var prop = t.GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (prop is null) continue;
                var val = prop.GetValue(arg);
                if (val is System.Collections.IEnumerable e2)
                {
                    foreach (var item in e2)
                    {
                        if (item is Guid g && g != Guid.Empty) idsToRegister.Add(g);
                    }
                }
            }
        }

        if (idsToRegister.Count == 0) return;

        int registered = 0;
        foreach (var id in idsToRegister.Distinct())
        {
            if (_cache.TryGet(id, out var entry) && entry is { Kind: "track" })
            {
                if (_registrar.RegisterStubTrack(id, entry)) registered++;
            }
        }
        if (registered > 0)
        {
            _log.LogInformation("[mdiscover] playlist endpoint: pre-registered {N} stub tracks (of {Total} ids in request)", registered, idsToRegister.Count);
        }
    }

    /// <summary>
    /// Matches GET /Audio/{id}/Lyrics or GET /Items/{id}/Lyrics (and pulls
    /// out the GUID for the lookup).
    /// </summary>
    private static bool IsLyricsRoute(ActionExecutingContext ctx, out Guid itemId)
    {
        itemId = default;
        if (!HttpMethods.IsGet(ctx.HttpContext.Request.Method)) return false;
        var path = ctx.HttpContext.Request.Path.Value ?? string.Empty;
        if (path.IndexOf("/Lyrics", StringComparison.OrdinalIgnoreCase) < 0) return false;
        return TryGuidBeforeSegment(path, "/Lyrics", out itemId);
    }

    /// <summary>
    /// Build a LyricDto from LrcLib's response. Translates LRC-format
    /// timestamps ([mm:ss.xx]) into Jellyfin's tick-based LyricLine format.
    /// Returns null if LrcLib has no lyrics for this track.
    /// </summary>
    private async Task<MediaBrowser.Model.Lyrics.LyricDto?> BuildLyricDtoAsync(DiscoveryItemCache.Entry e, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(e.Artist) || string.IsNullOrWhiteSpace(e.TrackTitle)) return null;

        var lr = await _lrclib.GetAsync(e.Artist!, e.TrackTitle!, e.AlbumName, e.DurationSeconds, ct).ConfigureAwait(false);
        if (lr is null) return null;

        // Prefer synced (LRC) lyrics — clients can highlight the current line.
        // Fall back to plain text split on newlines.
        var lines = new List<MediaBrowser.Model.Lyrics.LyricLine>();
        if (!string.IsNullOrWhiteSpace(lr.SyncedLyrics))
        {
            foreach (var (ticks, text) in ParseLrc(lr.SyncedLyrics!))
            {
                lines.Add(new MediaBrowser.Model.Lyrics.LyricLine(text, ticks, null));
            }
        }
        else if (!string.IsNullOrWhiteSpace(lr.PlainLyrics))
        {
            foreach (var line in lr.PlainLyrics!.Split('\n'))
            {
                var trimmed = line.TrimEnd('\r');
                lines.Add(new MediaBrowser.Model.Lyrics.LyricLine(trimmed, null, null));
            }
        }
        if (lines.Count == 0) return null;

        return new MediaBrowser.Model.Lyrics.LyricDto
        {
            Metadata = new MediaBrowser.Model.Lyrics.LyricMetadata
            {
                Artist = e.Artist,
                Album = e.AlbumName,
                Title = e.TrackTitle,
                Length = e.DurationSeconds.HasValue
                    ? (long?)TimeSpan.FromSeconds(e.DurationSeconds.Value).Ticks
                    : null,
                IsSynced = !string.IsNullOrWhiteSpace(lr.SyncedLyrics),
            },
            Lyrics = lines,
        };
    }

    /// <summary>
    /// Parse LRC format: [mm:ss.xx]Lyric text → (ticks, text). Multiple
    /// timestamps on a single line ([00:14.20][01:02.45]Same chorus) all
    /// emit the same text at each timestamp. Lines with no timestamp are
    /// dropped (header lines like [ar:Artist] aren't user-visible lyrics).
    /// </summary>
    private static IEnumerable<(long ticks, string text)> ParseLrc(string lrc)
    {
        var rx = new System.Text.RegularExpressions.Regex(
            @"\[(\d{1,2}):(\d{1,2})(?:\.(\d{1,3}))?\]",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        foreach (var raw in lrc.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var matches = rx.Matches(line);
            if (matches.Count == 0) continue;
            // Text is whatever follows the LAST timestamp on this line.
            var lastEnd = matches[matches.Count - 1].Index + matches[matches.Count - 1].Length;
            var text = lastEnd < line.Length ? line.Substring(lastEnd).Trim() : string.Empty;
            // Drop pure-metadata lines like [ar:...] — those won't have a colon-separated minute:second pattern that survives the regex (the regex requires digits in both groups, so [ar:foo] doesn't match anyway). Defensive empty-text drop.
            if (string.IsNullOrEmpty(text)) continue;

            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                if (!int.TryParse(m.Groups[1].Value, out var min)) continue;
                if (!int.TryParse(m.Groups[2].Value, out var sec)) continue;
                int hundredths = 0;
                if (m.Groups[3].Success) int.TryParse(m.Groups[3].Value.PadRight(2, '0').Substring(0, 2), out hundredths);
                var ms = (min * 60 * 1000) + (sec * 1000) + (hundredths * 10);
                yield return (TimeSpan.FromMilliseconds(ms).Ticks, text);
            }
        }
    }

    /// <summary>
    /// /Sessions/Playing — start, /Sessions/Playing/Progress, /Sessions/Playing/Stopped,
    /// /Sessions/Playing/Ping. The client posts these as the queue advances so the
    /// server's session state can track now-playing.
    /// </summary>
    private static bool IsSessionsPlayingRoute(ActionExecutingContext ctx)
    {
        var path = ctx.HttpContext.Request.Path.Value ?? string.Empty;
        // Match exactly /Sessions/Playing or /Sessions/Playing/<verb>, not just any path.
        if (path.IndexOf("/Sessions/Playing", StringComparison.OrdinalIgnoreCase) < 0) return false;
        // Method is always POST for these.
        return HttpMethods.IsPost(ctx.HttpContext.Request.Method);
    }

    /// <summary>
    /// /Sessions/Playing posts include the item-id in the request body (a
    /// PlaybackStartInfo / PlaybackProgressInfo / PlaybackStopInfo DTO). Pull
    /// it via reflection from the bound action arguments. The body has been
    /// model-bound by the time action filters run, so the DTO is in
    /// ctx.ActionArguments.
    /// </summary>
    private static bool TryExtractSessionItemId(ActionExecutingContext ctx, out Guid itemId)
    {
        itemId = default;
        foreach (var arg in ctx.ActionArguments.Values)
        {
            if (arg is null) continue;
            // Walk every public instance property on the bound model looking
            // for "ItemId" of type Guid.
            var prop = arg.GetType().GetProperty("ItemId",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (prop is null) continue;
            var raw = prop.GetValue(arg);
            if (raw is Guid g && g != Guid.Empty) { itemId = g; return true; }
            if (raw is string s && Guid.TryParse(s, out var gp)) { itemId = gp; return true; }
        }
        return false;
    }

    /// <summary>
    /// When a stub track's audio is fetched, register a synthetic Audio entity
    /// in Jellyfin's in-memory library cache (so GetItemById finds it) and fire
    /// ISessionManager.OnPlaybackStart for it. This causes Jellyfin to update
    /// the session's NowPlayingItem and broadcast a SessionsUpdated event over
    /// WebSocket — which is what physical-track playback relies on for the
    /// client's now-playing UI to refresh on auto-advance.
    ///
    /// Best-effort, fully wrapped in try/catch — if any of this fails, the
    /// audio still streams; we just don't get the UI nudge.
    /// </summary>
    private async Task NudgeSessionPlaybackStartAsync(HttpContext httpCtx, Guid stubId, DiscoveryItemCache.Entry entry)
    {
        try
        {
            // 1. Ensure synthetic Audio is registered (idempotent via registrar).
            //    Most stubs are pre-registered at synthesis time, but this is a
            //    safety net in case a track somehow hits audio fetch first.
            _registrar.RegisterStubTrack(stubId, entry);

            // 2. Get-or-create the session for this request. Audio fetches
            // bypass the normal session-creation paths in Jellyfin's controllers,
            // so ISessionManager.Sessions can be empty even though the user is
            // actively playing. LogSessionActivity is what Jellyfin's controllers
            // use internally — it creates the session if needed and refreshes it
            // if it exists.
            var auth = await _authContext.GetAuthorizationInfo(httpCtx.Request).ConfigureAwait(false);
            _log.LogInformation("[mdiscover] session nudge: auth lookup → device={Device} user={User} client={Client}",
                auth?.DeviceId, auth?.UserId, auth?.Client);
            if (auth?.User is null)
            {
                _log.LogInformation("[mdiscover] session nudge: no User on auth info — skipping");
                return;
            }

            var session = await _sessionManager.LogSessionActivity(
                auth.Client,
                auth.Version,
                auth.DeviceId,
                auth.Device,
                httpCtx.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
                auth.User).ConfigureAwait(false);
            if (session is null)
            {
                _log.LogInformation("[mdiscover] session nudge: LogSessionActivity returned null — skipping");
                return;
            }
            _log.LogInformation("[mdiscover] session nudge: session resolved id={Id} client={Client}",
                session.Id, session.Client);

            // 3. Fire OnPlaybackStart with the stub's ID. ISessionManager will
            // GetItemById(stubId) — which now returns the synthetic Audio we
            // just registered — and broadcast the session update.
            var info = new MediaBrowser.Model.Session.PlaybackStartInfo
            {
                ItemId = stubId,
                SessionId = session.Id,
                MediaSourceId = stubId.ToString("N"),
                CanSeek = true,
                IsPaused = false,
                IsMuted = false,
                PlayMethod = MediaBrowser.Model.Session.PlayMethod.DirectStream,
                PositionTicks = 0,
            };
            await _sessionManager.OnPlaybackStart(info).ConfigureAwait(false);
            _log.LogInformation("[mdiscover] session nudge: OnPlaybackStart fired for stub {Id} on session {Session}", stubId, session.Id);

            // Record this play into our recently-played tracker so the home-
            // screen Recently Played row can include this stub.
            var albumIdForRecent = entry.DeezerAlbumId is { } dzAlbId
                ? Services.DiscoveryManager.StubGuid("dz-album", dzAlbId.ToString())
                : (Guid?)null;
            _recents.RecordPlay(auth.UserId, stubId, albumIdForRecent);
            _log.LogInformation("[mdiscover] recently-played: recorded {Track} (album={Album}) for user {User}",
                stubId, albumIdForRecent, auth.UserId);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[mdiscover] session nudge failed (non-fatal)");
        }
    }

    /// <summary>
    /// Pipe ytmusic-stream-server's audio response straight through Jellyfin's
    /// HTTP response — same-origin, byte-identical, including Range support.
    ///
    /// Critical detail: we set Content-Length via the explicit Response.ContentLength
    /// API (not the headers dictionary), and we cap the byte count on the body
    /// copy. Without this, Kestrel can fall back to Transfer-Encoding: chunked,
    /// which leaves the audio engine without a definitive end-of-stream — the
    /// player keeps reading bytes (even merging into a prefetched next track's
    /// stream) and never fires the "ended" event that triggers the queue's UI
    /// to advance. That's why Finer's now-playing card gets stuck on the
    /// previous track and the elapsed counter overruns the runtime.
    /// </summary>
    private async Task ProxyAudioAsync(HttpContext httpCtx, string upstreamUrl)
    {
        try
        {
            using var http = _httpFactory.CreateClient();
            http.Timeout = Timeout.InfiniteTimeSpan; // streaming response

            using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, upstreamUrl);
            if (httpCtx.Request.Headers.TryGetValue("Range", out var range) && !string.IsNullOrWhiteSpace(range))
                req.Headers.TryAddWithoutValidation("Range", range.ToString());

            using var upstream = await http.SendAsync(req, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, httpCtx.RequestAborted).ConfigureAwait(false);

            httpCtx.Response.StatusCode = (int)upstream.StatusCode;

            // Set Content-Length via the explicit API — this prevents Kestrel
            // from falling back to chunked-transfer-encoding, which would leave
            // the audio engine without a definitive EOF.
            var contentLength = upstream.Content.Headers.ContentLength;
            if (contentLength.HasValue)
            {
                httpCtx.Response.ContentLength = contentLength.Value;
            }

            // Forward content-related headers, but skip the ones we set via
            // dedicated API (Content-Length) and ones that would conflict
            // with Kestrel's handling (Transfer-Encoding).
            foreach (var h in upstream.Content.Headers)
            {
                if (h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                if (h.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)) continue;
                httpCtx.Response.Headers[h.Key] = h.Value.ToArray();
            }
            if (upstream.Headers.TryGetValues("Accept-Ranges", out var ar))
                httpCtx.Response.Headers["Accept-Ranges"] = ar.ToArray();
            else
                httpCtx.Response.Headers["Accept-Ranges"] = "bytes";

            await using var stream = await upstream.Content.ReadAsStreamAsync(httpCtx.RequestAborted).ConfigureAwait(false);

            // If we know the exact byte count, cap the copy at that length so
            // we don't accidentally bleed into anything beyond the file. This
            // also gives Kestrel a clean EOF at the expected position.
            if (contentLength.HasValue)
            {
                await CopyExactlyAsync(stream, httpCtx.Response.Body, contentLength.Value, httpCtx.RequestAborted).ConfigureAwait(false);
            }
            else
            {
                await stream.CopyToAsync(httpCtx.Response.Body, httpCtx.RequestAborted).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* client disconnected */ }
        catch (Exception ex) { _log.LogWarning(ex, "[mdiscover] proxy stream failed"); }
    }

    /// <summary>
    /// Copy exactly `length` bytes from source to destination. Stops at exactly
    /// `length` bytes regardless of whether source has more (which it shouldn't
    /// if Content-Length is honest, but defense in depth never hurts).
    /// </summary>
    private static async Task CopyExactlyAsync(System.IO.Stream src, System.IO.Stream dst, long length, CancellationToken ct)
    {
        const int bufSize = 64 * 1024;
        var buf = new byte[bufSize];
        long remaining = length;
        while (remaining > 0)
        {
            var toRead = (int)Math.Min(remaining, bufSize);
            var read = await src.ReadAsync(buf.AsMemory(0, toRead), ct).ConfigureAwait(false);
            if (read == 0) break; // upstream EOF before expected
            await dst.WriteAsync(buf.AsMemory(0, read), ct).ConfigureAwait(false);
            remaining -= read;
        }
    }

    private static bool IsVideoStreamRoute(ActionExecutingContext ctx)
    {
        var path = ctx.HttpContext.Request.Path.Value ?? string.Empty;
        // Web/native variants for video playback. /Items/{id}/File catches
        // Finer/iOS download-style requests.
        if (path.StartsWith("/Videos/", StringComparison.OrdinalIgnoreCase)
            && path.Length > "/Videos/".Length + 32)
            return true;
        if (path.StartsWith("/Items/", StringComparison.OrdinalIgnoreCase)
            && (path.EndsWith("/File", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/Download", StringComparison.OrdinalIgnoreCase)))
        {
            // Note: same paths the audio resolver checks. The branching
            // happens by Kind on the cache entry — Kind:"video" goes here,
            // Kind:"track" goes through the audio path.
            return true;
        }
        return false;
    }

    private static bool IsAudioStreamRoute(ActionExecutingContext ctx)
    {
        var path = ctx.HttpContext.Request.Path.Value ?? string.Empty;
        // Any /Audio/{id}/<verb> shape — web client uses several variants:
        //   /Audio/{id}/universal       (initial discovery)
        //   /Audio/{id}/stream          (legacy direct stream)
        //   /Audio/{id}/stream.{ext}    (stream with extension hint)
        //   /Audio/{id}/main.{ext}      (direct-play redirect target)
        //   /Audio/{id}/master.m3u8     (HLS playlist)
        //   /Audio/{id}/hls1/...        (HLS segments)
        // We proxy all of them through ytmusic-stream-server (which serves
        // self-contained MP3, so HLS-specific paths get the same MP3 stream
        // and the audio element handles it as direct play).
        if (path.StartsWith("/Audio/", StringComparison.OrdinalIgnoreCase)
            && path.Length > "/Audio/".Length + 32)
        {
            return true;
        }
        // Finamp / iOS Jellyfin app: /Items/{id}/File or /Items/{id}/Download.
        if (path.StartsWith("/Items/", StringComparison.OrdinalIgnoreCase)
            && (path.EndsWith("/File", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/Download", StringComparison.OrdinalIgnoreCase)))
            return true;
        return false;
    }

    private static bool TryExtractItemIdFromPath(ActionExecutingContext ctx, out Guid id)
    {
        id = default;
        var route = ctx.RouteData.Values;
        // Try the obvious route slots first.
        foreach (var key in new[] { "itemId", "ItemId", "id", "Id" })
            if (route.TryGetValue(key, out var v) && v is not null && Guid.TryParse(v.ToString(), out id)) return true;

        // Fallback: parse the URL path itself — /Items/{guid}/PlaybackInfo or /Audio/{guid}/...
        var path = ctx.HttpContext.Request.Path.Value ?? string.Empty;
        var parts = path.Trim('/').Split('/');
        for (var i = 0; i + 1 < parts.Length; i++)
        {
            var seg = parts[i];
            if ((string.Equals(seg, "Items", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(seg, "Audio", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(seg, "Videos", StringComparison.OrdinalIgnoreCase))
                && Guid.TryParse(parts[i + 1], out id))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Detects GET /Playlists/{guid}/Items where the guid is one of our
    /// synthetic discovery playlists (Top 100 Global, etc.).
    /// </summary>
    private bool TryExtractDiscoveryPlaylistChildrenId(ActionExecutingContext ctx, out Guid id)
    {
        id = default;
        if (!HttpMethods.IsGet(ctx.HttpContext.Request.Method)) return false;
        var path = ctx.HttpContext.Request.Path.Value ?? string.Empty;
        if (path.IndexOf("/Playlists/", StringComparison.OrdinalIgnoreCase) < 0) return false;
        if (!path.EndsWith("/Items", StringComparison.OrdinalIgnoreCase)) return false;
        if (!TryGuidAfterSegment(path, "/Playlists/", out var guid)) return false;
        if (_discoveryFeed.Get(guid) is null) return false;
        id = guid;
        return true;
    }

    /// <summary>
    /// Build a Playlist BaseItemDto for one of our synthetic discovery feeds.
    /// </summary>
    private static BaseItemDto BuildDiscoveryPlaylistDto(DiscoveryPlaylistFeed.PlaylistDto p)
    {
        var imageTags = !string.IsNullOrEmpty(p.ImageUrl)
            ? new Dictionary<ImageType, string> { { ImageType.Primary, "discover-" + p.Id.ToString("N") } }
            : new Dictionary<ImageType, string>();

        return new BaseItemDto
        {
            Id = p.Id,
            ServerId = Plugin.ServerSystemId,
            Name = p.Name,
            Type = BaseItemKind.Playlist,
            MediaType = MediaType.Audio,
            Tags = new[] { Tags.Stub, "Discovery" },
            ImageTags = imageTags,
            ImageBlurHashes = new() { { ImageType.Primary, new Dictionary<string, string>() } },
            PrimaryImageAspectRatio = 1.0,
            ChildCount = p.TrackIds.Count,
            CumulativeRunTimeTicks = null,
            IsFolder = true,
            CanDelete = false,
            CanDownload = false,
            Overview = p.SubLabel,
            DateCreated = p.FetchedAt,
            Genres = Array.Empty<string>(),
            GenreItems = Array.Empty<NameGuidPair>(),
            Studios = Array.Empty<NameGuidPair>(),
            Artists = Array.Empty<string>(),
            ArtistItems = Array.Empty<NameGuidPair>(),
            AlbumArtists = Array.Empty<NameGuidPair>(),
            BackdropImageTags = Array.Empty<string>(),
            LockedFields = Array.Empty<MediaBrowser.Model.Entities.MetadataField>(),
            ProviderIds = new Dictionary<string, string>(),
        };
    }

    /// <summary>
    /// Build a single MediaSourceInfo for a stub track. Used in two places:
    /// the PlaybackInfo POST response, AND the BaseItemDto.MediaSources
    /// array for tap-to-play (which doesn't go through PlaybackInfo).
    /// </summary>
    public static MediaSourceInfo BuildMediaSourceForTrack(Guid id, DiscoveryItemCache.Entry e)
    {
        var runtimeTicks = e.DurationSeconds.HasValue
            ? (long?)TimeSpan.FromSeconds(e.DurationSeconds.Value).Ticks
            : null;

        // PlaybackInfo metadata MUST match the bytes we actually serve.
        // ytmusic-stream-server transcodes everything to MP3 (192kbps stereo)
        // so the audio engine can detect end-of-stream cleanly.
        //
        // Protocol=File + IsRemote=false matches what Jellyfin returns for
        // physical-library tracks. Web's player has a path that uses
        // /Audio/{id}/universal for File-protocol sources, but treats Http+
        // IsRemote=true as "external URL only" — and rejects it if no usable
        // direct-stream URL is set on the source.
        var audioStream = new MediaStream
        {
            Type = MediaStreamType.Audio,
            Codec = "mp3",
            BitRate = 192_000,
            SampleRate = 44_100,
            Channels = 2,
            ChannelLayout = "stereo",
            Index = 0,
            IsDefault = true,
        };

        return new MediaSourceInfo
        {
            Id = id.ToString("N"),
            // Use a filesystem-ish path that doesn't start with /mdiscover/.
            // Web's tap-to-play takes MediaSourceInfo.Path and tries to use
            // it as the audio src URL if it looks like one (anything that
            // begins with "/" can resolve relative to the Jellyfin origin).
            // /data/... is what Jellyfin returns for real items — web sees
            // it as a server-side filesystem path, not a URL, and falls
            // through to /Audio/{id}/universal instead.
            Path = $"/data/music/streamed/{id:N}.mp3",
            Protocol = MediaProtocol.File,
            IsRemote = false,
            Type = MediaSourceType.Default,
            Name = e.TrackTitle ?? "(unknown track)",
            Container = "mp3",
            SupportsTranscoding = true,
            SupportsDirectStream = true,
            SupportsDirectPlay = true,
            RunTimeTicks = runtimeTicks,
            Bitrate = 192_000,
            DefaultAudioStreamIndex = 0,
            MediaStreams = new List<MediaStream> { audioStream },
            MediaAttachments = Array.Empty<MediaBrowser.Model.Entities.MediaAttachment>(),
            Formats = Array.Empty<string>(),
            RequiredHttpHeaders = new Dictionary<string, string>(),
        };
    }

    private static PlaybackInfoResponse BuildPlaybackInfoForTrack(Guid id, DiscoveryItemCache.Entry e)
    {
        return new PlaybackInfoResponse
        {
            MediaSources = new[] { BuildMediaSourceForTrack(id, e) },
            PlaySessionId = Guid.NewGuid().ToString("N"),
        };
    }

    /// <summary>
    /// PlaybackInfoResponse for a synthetic MusicVideo. Same shape as the
    /// audio path but with video MediaSource (mp4/h264/aac, Path-based so
    /// web's video player routes through /Videos/{id}/... → our resolver
    /// → Python /video/by-id endpoint.)
    /// </summary>
    private static PlaybackInfoResponse BuildPlaybackInfoForVideo(Guid id, DiscoveryItemCache.Entry e)
    {
        var videoStream = new MediaStream
        {
            Type = MediaStreamType.Video,
            Codec = "h264",
            Width = 480,
            Height = 360,
            RealFrameRate = 30f,
            Index = 0,
            IsDefault = true,
        };
        var audioStream = new MediaStream
        {
            Type = MediaStreamType.Audio,
            Codec = "aac",
            BitRate = 128_000,
            SampleRate = 44_100,
            Channels = 2,
            ChannelLayout = "stereo",
            Index = 1,
            IsDefault = true,
        };
        var src = new MediaSourceInfo
        {
            Id = id.ToString("N"),
            Path = $"/data/musicvideos/streamed/{id:N}.mp4",
            Protocol = MediaProtocol.File,
            IsRemote = false,
            Type = MediaSourceType.Default,
            Name = e.TrackTitle ?? "(unknown video)",
            Container = "mp4",
            SupportsTranscoding = true,
            SupportsDirectStream = true,
            SupportsDirectPlay = true,
            RunTimeTicks = e.DurationSeconds.HasValue
                ? (long?)TimeSpan.FromSeconds(e.DurationSeconds.Value).Ticks
                : null,
            Bitrate = 1_000_000,
            DefaultAudioStreamIndex = 1,
            MediaStreams = new List<MediaStream> { videoStream, audioStream },
            MediaAttachments = Array.Empty<MediaBrowser.Model.Entities.MediaAttachment>(),
            Formats = Array.Empty<string>(),
            RequiredHttpHeaders = new Dictionary<string, string>(),
        };
        return new PlaybackInfoResponse
        {
            MediaSources = new[] { src },
            PlaySessionId = Guid.NewGuid().ToString("N"),
        };
    }

    private static HashSet<string> ExtractIncludeItemTypes(HttpContext http)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "includeItemTypes", "IncludeItemTypes" })
        {
            if (http.Request.Query.TryGetValue(key, out var qs))
                foreach (var t in qs.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    result.Add(t);
        }
        return result;
    }

    private static bool TryExtractBulkIdsQuery(HttpContext http, out List<Guid> ids)
    {
        ids = new List<Guid>();
        // Two shapes Jellyfin's web client uses for bulk lookups:
        //   /Items?ids=g1,g2,...                         (Items controller)
        //   /Users/{userId}/Items?Ids=g1,g2,...          (UserLibrary controller)
        // Both end in "/Items" with the ids in the query. We accept either.
        // Pattern explicitly excludes /Items/{id}/... — that's a sub-action,
        // not a bulk lookup.
        var path = (http.Request.Path.Value ?? string.Empty).TrimEnd('/');
        bool isBulkPath = string.Equals(path, "/Items", StringComparison.OrdinalIgnoreCase)
            || (path.StartsWith("/Users/", StringComparison.OrdinalIgnoreCase)
                && path.EndsWith("/Items", StringComparison.OrdinalIgnoreCase));
        if (!isBulkPath) return false;

        foreach (var k in new[] { "ids", "Ids" })
        {
            if (http.Request.Query.TryGetValue(k, out var qs))
                foreach (var part in qs.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (Guid.TryParse(part, out var g)) ids.Add(g);
        }
        return ids.Count > 0;
    }

    private static bool TryExtractGuidListFromQuery(HttpContext http, string key, out List<Guid> ids)
    {
        ids = new List<Guid>();
        // ASP.NET case-sensitive matching: try both casings (UpperFirst is the doc convention; the SDK sends lower).
        foreach (var k in new[] { key, char.ToUpperInvariant(key[0]) + key[1..] })
        {
            if (http.Request.Query.TryGetValue(k, out var qs))
            {
                foreach (var part in qs.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    if (Guid.TryParse(part, out var g)) ids.Add(g);
            }
        }
        return ids.Count > 0;
    }

    private static BaseItemDto BuildDtoFromCache(Guid id, DiscoveryItemCache.Entry e)
    {
        var imageTags = new Dictionary<ImageType, string> { { ImageType.Primary, "deezer-" + id.ToString("N") } };
        var blurHashes = new Dictionary<ImageType, Dictionary<string, string>> { { ImageType.Primary, new Dictionary<string, string>() } };

        NameGuidPair[]? artistPair = null;
        if (e.Artist is { Length: > 0 } artistName)
        {
            // Mobile clients (Finer, Jellyfin Mobile) read the artist label
            // from ArtistItems/AlbumArtists (NameGuidPair lists), not from
            // the plain Artists string array — so we always need to build
            // a pair when we have a name. For Deezer-sourced tracks we use
            // the stable Deezer artist guid; for YT-Music or anything
            // else without a source artist id, we synthesize a stable guid
            // from the name so navigation by id at least round-trips.
            var pairId = e.DeezerArtistId is long dzArtistId
                ? DiscoveryManager.StubGuid("dz-artist", dzArtistId.ToString())
                : DiscoveryManager.StubGuid("name-artist", artistName);
            artistPair = new[] { new NameGuidPair { Name = artistName, Id = pairId } };
        }

        return e.Kind switch
        {
            "artist" => MusicSearchActionFilter.BuildArtistDto(id, e.Artist),
            "album" => new BaseItemDto
            {
                Id = id,
                ServerId = Plugin.ServerSystemId,
                Name = e.AlbumName ?? "(unknown album)",
                Type = BaseItemKind.MusicAlbum,
                MediaType = MediaType.Unknown,
                Tags = new[] { Tags.Stub },
                ImageTags = imageTags,
                ImageBlurHashes = blurHashes,
                PrimaryImageAspectRatio = 1.0,
                Artists = e.Artist is { Length: > 0 } an ? new[] { an } : null,
                AlbumArtist = e.Artist,
                AlbumArtists = artistPair ?? Array.Empty<NameGuidPair>(),
                ArtistItems = artistPair ?? Array.Empty<NameGuidPair>(),
                IsFolder = true,
                ProviderIds = e.DeezerAlbumId is long dzAlbId
                    ? new Dictionary<string, string> { { ProviderIds.DeezerAlbum, dzAlbId.ToString() } }
                    : null,
            },
            "track" => new BaseItemDto
            {
                Id = id,
                ServerId = Plugin.ServerSystemId,
                Name = e.TrackTitle ?? "(unknown track)",
                Type = BaseItemKind.Audio,
                MediaType = MediaType.Audio,
                Tags = new[] { Tags.Stub },
                ImageTags = imageTags,
                ImageBlurHashes = blurHashes,
                PrimaryImageAspectRatio = 1.0,
                // Always concrete arrays — web's tap-to-play handler iterates
                // these (Artists, ArtistItems, AlbumArtists, Genres, ...) and
                // throws "undefined is not an object (evaluating '.length')"
                // if any are null.
                Artists = e.Artist is { Length: > 0 } an ? new[] { an } : Array.Empty<string>(),
                ArtistItems = artistPair ?? Array.Empty<NameGuidPair>(),
                AlbumArtists = artistPair ?? Array.Empty<NameGuidPair>(),
                Genres = Array.Empty<string>(),
                GenreItems = Array.Empty<NameGuidPair>(),
                Studios = Array.Empty<NameGuidPair>(),
                Album = e.AlbumName,
                AlbumArtist = e.Artist,
                AlbumId = e.DeezerAlbumId is long dzAlbId2
                    ? DiscoveryManager.StubGuid("dz-album", dzAlbId2.ToString())
                    : (Guid?)null,
                AlbumPrimaryImageTag = e.DeezerAlbumId is long dzAlbId3
                    ? "deezer-" + DiscoveryManager.StubGuid("dz-album", dzAlbId3.ToString()).ToString("N")
                    : null,
                RunTimeTicks = e.DurationSeconds.HasValue
                    ? (long?)TimeSpan.FromSeconds(e.DurationSeconds.Value).Ticks : null,
                IsFolder = false,
                CanDownload = false,
                LocationType = MediaBrowser.Model.Entities.LocationType.FileSystem,
                ProviderIds = e.DeezerTrackId is long dzTrId
                    ? new Dictionary<string, string> { { ProviderIds.DeezerTrack, dzTrId.ToString() } }
                    : new Dictionary<string, string>(),
                // MediaSources required for album-play / three-dot Play to work
                // (those flows queue items and read MediaSources from each).
                MediaSources = new[] { BuildMediaSourceForTrack(id, e) },
                MediaStreams = new[]
                {
                    new MediaStream
                    {
                        Type = MediaStreamType.Audio,
                        Codec = "mp3",
                        BitRate = 192_000,
                        SampleRate = 44_100,
                        Channels = 2,
                        ChannelLayout = "stereo",
                        Index = 0,
                        IsDefault = true,
                    }
                },
                BackdropImageTags = Array.Empty<string>(),
                LockedFields = Array.Empty<MediaBrowser.Model.Entities.MetadataField>(),
            },
            // MusicVideo stub — delegate to the search filter's video DTO
            // builder so the same shape is returned regardless of which
            // code path hit /Items/{id}. Without this case the switch falls
            // through to the "unknown stub" default and the web video
            // player receives an empty DTO and spins forever.
            "video" => MusicSearchActionFilter.BuildMusicVideoDto(id, e),
            _ => new BaseItemDto { Id = id, Name = "(unknown stub)", Tags = new[] { Tags.Stub } },
        };
    }
}
