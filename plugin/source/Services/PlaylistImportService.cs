using System.Globalization;
using System.Text;
using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Playlists;
using Microsoft.Extensions.Logging;

namespace JellyMusicDiscovery.Services;

public sealed class PlaylistImportService
{
    private const int MaxAlbumsPerRun = 10;
    private const int InitialAlbumsPerImport = 3;
    private const int MaxAlbumAttempts = 3;
    private readonly ILibraryManager _library;
    private readonly IPlaylistManager _playlists;
    private readonly IUserManager _users;
    private readonly LidarrClient _lidarr;
    private readonly ILogger<PlaylistImportService> _log;
    private readonly string? _jobsFile;
    private readonly SemaphoreSlim _importGate = new(1, 1);
    private readonly SemaphoreSlim _jobsGate = new(1, 1);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public PlaylistImportService(ILibraryManager library, IPlaylistManager playlists, IUserManager users,
        LidarrClient lidarr, ILogger<PlaylistImportService> log)
    {
        _library = library;
        _playlists = playlists;
        _users = users;
        _lidarr = lidarr;
        _log = log;
        var dir = Plugin.Instance?.DataFolderPath;
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
            _jobsFile = Path.Combine(dir, "playlist-import-jobs.json");
        }
    }

    public IReadOnlyList<object> Users() => _users.GetUsers()
        .OrderBy(u => u.Username)
        .Select(u => (object)new { id = u.Id, name = u.Username })
        .ToArray();

    public ImportPreview Preview(string fileName, byte[] data)
    {
        var tracks = Parse(fileName, data);
        var matches = Match(tracks);
        return Summarize(Path.GetFileNameWithoutExtension(fileName), tracks, matches);
    }

    public async Task<ImportResult> ImportAsync(string fileName, byte[] data, Guid userId,
        string? playlistName, bool requestMissingAlbums, CancellationToken ct)
    {
        if (_users.GetUserById(userId) is null) throw new ArgumentException("Unknown Jellyfin user.");
        var tracks = Parse(fileName, data);
        var name = CleanPlaylistName(playlistName ?? Path.GetFileNameWithoutExtension(fileName));
        await _importGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ApplyAsync(new ImportJob
            {
                UserId = userId,
                PlaylistName = name,
                Tracks = tracks,
                RequestMissingAlbums = requestMissingAlbums,
            },
                requestMissingAlbums, saveJob: true, InitialAlbumsPerImport, ct).ConfigureAwait(false);
        }
        finally { _importGate.Release(); }
    }

    public async Task<IReadOnlyList<ImportResult>> ReconcileAsync(CancellationToken ct)
    {
        await _importGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var jobs = await LoadJobsAsync(ct).ConfigureAwait(false);
            var results = new List<ImportResult>();
            var active = new List<ImportJob>();
            var albumBudget = MaxAlbumsPerRun;
            foreach (var job in jobs)
            {
                var result = await ApplyAsync(job, job.RequestMissingAlbums, saveJob: false, albumBudget, ct).ConfigureAwait(false);
                albumBudget -= result.AlbumRequests.Count;
                results.Add(result);
                if (result.Preview.Matched < result.Preview.Total) active.Add(job);
            }
            await SaveJobsAsync(active, ct).ConfigureAwait(false);
            return results;
        }
        finally { _importGate.Release(); }
    }

    private async Task<ImportResult> ApplyAsync(ImportJob job, bool requestMissingAlbums, bool saveJob,
        int albumRequestLimit, CancellationToken ct)
    {
        var matches = Match(job.Tracks);
        var ids = matches.Where(m => m.Item is not null).Select(m => m.Item!.Id).Distinct().ToArray();
        var playlist = _playlists.GetPlaylists(job.UserId)
            .FirstOrDefault(p => string.Equals(p.Name, job.PlaylistName, StringComparison.OrdinalIgnoreCase));

        Guid playlistId;
        if (playlist is null)
        {
            var created = await _playlists.CreatePlaylist(new PlaylistCreationRequest
            {
                Name = job.PlaylistName,
                ItemIdList = ids,
                UserId = job.UserId,
                MediaType = MediaType.Audio,
                Users = [],
                Public = false,
            }).ConfigureAwait(false);
            playlistId = Guid.Parse(created.Id);
        }
        else
        {
            await _playlists.UpdatePlaylist(new PlaylistUpdateRequest
            {
                UserId = job.UserId,
                Id = playlist.Id,
                Name = job.PlaylistName,
                Ids = ids,
                Users = playlist.Shares,
                Public = playlist.OpenAccess,
            }).ConfigureAwait(false);
            playlistId = playlist.Id;
        }

        var albumResults = new List<AlbumRequest>();
        if (requestMissingAlbums && albumRequestLimit > 0)
        {
            var albums = matches.Where(m => m.Item is null && !m.Ambiguous)
                .Select(m => m.Source)
                .Where(t => !string.IsNullOrWhiteSpace(t.Artist) && !string.IsNullOrWhiteSpace(t.Album))
                .DistinctBy(t => $"{Normalize(t.Artist)}|{Normalize(t.Album)}")
                .Where(t => !job.RequestedAlbums.Contains(AlbumKey(t))
                    && job.AlbumAttempts.GetValueOrDefault(AlbumKey(t)) < MaxAlbumAttempts)
                .Take(albumRequestLimit)
                .ToArray();
            foreach (var album in albums)
            {
                var result = await _lidarr.AddAlbumByTextAsync(album.Artist!, album.Album!, ct).ConfigureAwait(false);
                albumResults.Add(new AlbumRequest(album.Artist!, album.Album!, result.Success,
                    result.Retryable, result.Message));
                var key = AlbumKey(album);
                job.AlbumAttempts[key] = result.Retryable
                    ? job.AlbumAttempts.GetValueOrDefault(key) + 1
                    : MaxAlbumAttempts;
                if (result.Success) job.RequestedAlbums.Add(key);
            }
        }

        var preview = Summarize(job.PlaylistName, job.Tracks, matches);
        if (saveJob) await SaveJobAsync(job, preview.Matched < preview.Total, ct).ConfigureAwait(false);
        var pendingAlbums = preview.MissingAlbums.Count(album =>
        {
            var key = AlbumKey(album.Artist, album.Album);
            return !job.RequestedAlbums.Contains(key)
                && job.AlbumAttempts.GetValueOrDefault(key) < MaxAlbumAttempts;
        });
        var unmatched = matches.Where(match => match.Item is null).ToArray();
        bool RequiresManualAction(TrackMatch match) => !requestMissingAlbums
            || match.Ambiguous
            || string.IsNullOrWhiteSpace(match.Source.Artist)
            || string.IsNullOrWhiteSpace(match.Source.Album)
            || (!job.RequestedAlbums.Contains(AlbumKey(match.Source))
                && job.AlbumAttempts.GetValueOrDefault(AlbumKey(match.Source)) >= MaxAlbumAttempts);
        var manual = unmatched.Where(RequiresManualAction).Select(ToMissingTrack).ToArray();
        var queued = unmatched.Where(match => !RequiresManualAction(match)).Select(ToMissingTrack).ToArray();
        _log.LogInformation("Imported playlist {Playlist}: {Matched}/{Total} matched, {Albums} albums requested",
            job.PlaylistName, preview.Matched, preview.Total, albumResults.Count);
        return new ImportResult(playlistId, preview, albumResults, pendingAlbums, queued, manual);
    }

    private List<TrackMatch> Match(IReadOnlyList<ImportTrack> source)
    {
        var audio = _library.GetItemList(new InternalItemsQuery
        {
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.Audio],
            IsVirtualItem = false,
        }).OfType<Audio>()
            .Where(item => !string.IsNullOrWhiteSpace(item.Path))
            .ToArray();

        var byMetadata = audio.GroupBy(a => Key(a.Name, a.Artists.FirstOrDefault(), a.Album))
            .ToDictionary(g => g.Key, g => g.ToArray());
        var byTitleArtist = audio.GroupBy(a => Key(a.Name, a.Artists.FirstOrDefault(), null))
            .ToDictionary(g => g.Key, g => g.ToArray());
        var byTitleAlbum = audio.GroupBy(a => Key(a.Name, null, a.Album))
            .ToDictionary(g => g.Key, g => g.ToArray());
        var byTitle = audio.GroupBy(a => Normalize(a.Name))
            .ToDictionary(g => g.Key, g => g.ToArray());
        var byPath = audio.Where(a => !string.IsNullOrWhiteSpace(a.Path))
            .GroupBy(a => NormalizePath(a.Path)).ToDictionary(g => g.Key, g => g.ToArray());

        return source.Select(track =>
        {
            Audio[] candidates = [];
            if (!string.IsNullOrWhiteSpace(track.Path))
                candidates = byPath.GetValueOrDefault(NormalizePath(track.Path)) ?? [];
            if (candidates.Length != 1 && !string.IsNullOrWhiteSpace(track.Title))
                candidates = byMetadata.GetValueOrDefault(Key(track.Title, track.Artist, track.Album)) ?? [];
            if (candidates.Length != 1 && !string.IsNullOrWhiteSpace(track.Title) && !string.IsNullOrWhiteSpace(track.Artist))
                candidates = byTitleArtist.GetValueOrDefault(Key(track.Title, track.Artist, null)) ?? [];
            if (candidates.Length != 1 && !string.IsNullOrWhiteSpace(track.Title) && !string.IsNullOrWhiteSpace(track.Album))
                candidates = byTitleAlbum.GetValueOrDefault(Key(track.Title, null, track.Album)) ?? [];
            if (candidates.Length != 1 && !string.IsNullOrWhiteSpace(track.Title))
                candidates = byTitle.GetValueOrDefault(Normalize(track.Title)) ?? [];
            return new TrackMatch(track, candidates.Length == 1 ? candidates[0] : null, candidates.Length > 1);
        }).ToList();
    }

    private static ImportPreview Summarize(string name, IReadOnlyList<ImportTrack> tracks, IReadOnlyList<TrackMatch> matches)
    {
        var missing = matches.Where(m => m.Item is null).Select(ToMissingTrack).ToArray();
        var albums = matches.Where(m => m.Item is null && !m.Ambiguous && !string.IsNullOrWhiteSpace(m.Source.Artist) && !string.IsNullOrWhiteSpace(m.Source.Album))
            .Select(m => new MissingAlbum(m.Source.Artist!, m.Source.Album!))
            .DistinctBy(a => $"{Normalize(a.Artist)}|{Normalize(a.Album)}").ToArray();
        return new ImportPreview(name, tracks.Count, matches.Count(m => m.Item is not null), missing, albums);
    }

    private static MissingTrack ToMissingTrack(TrackMatch match) => new(
        match.Source.Title ?? Path.GetFileNameWithoutExtension(match.Source.Path),
        match.Source.Artist, match.Source.Album, match.Ambiguous);

    private static List<ImportTrack> Parse(string fileName, byte[] data)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (ext is not (".txt" or ".m3u" or ".m3u8" or ".csv"))
            throw new ArgumentException("Supported formats: .txt, .m3u, .m3u8, .csv");
        var text = Decode(data);
        return ext is ".m3u" or ".m3u8" ? ParseM3u(text) : ParseTable(text, ext == ".txt" ? '\t' : ',');
    }

    private static string Decode(byte[] data)
    {
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) return Encoding.Unicode.GetString(data, 2, data.Length - 2);
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
        if (data.Take(Math.Min(data.Length, 100)).Count(b => b == 0) > 10) return Encoding.Unicode.GetString(data);
        return new UTF8Encoding(false, true).GetString(data);
    }

    private static List<ImportTrack> ParseM3u(string text)
    {
        var result = new List<ImportTrack>();
        string? title = null, artist = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                var label = line[(line.IndexOf(',') + 1)..];
                var split = label.IndexOf(" - ", StringComparison.Ordinal);
                artist = split > 0 ? label[..split].Trim() : null;
                title = split > 0 ? label[(split + 3)..].Trim() : label.Trim();
            }
            else if (line.Length > 0 && !line.StartsWith('#'))
            {
                result.Add(new ImportTrack(title, artist, null, line));
                title = artist = null;
            }
        }
        return result;
    }

    private static List<ImportTrack> ParseTable(string text, char delimiter)
    {
        var rows = ParseDelimited(text, delimiter);
        if (rows.Count < 2) throw new ArgumentException("The playlist has no tracks.");
        var headers = rows[0].Select(NormalizeHeader).ToArray();
        int Find(params string[] names) => Array.FindIndex(headers, h => names.Contains(h));
        var title = Find("name", "trackname", "title");
        var artist = Find("artist", "artistname", "artistnames");
        var album = Find("album", "albumname");
        var location = Find("location", "path", "file");
        if (title < 0 && location < 0) throw new ArgumentException("No title or location column found.");
        string? Cell(string[] row, int i) => i >= 0 && i < row.Length && !string.IsNullOrWhiteSpace(row[i]) ? row[i].Trim() : null;
        return rows.Skip(1).Where(r => r.Any(v => !string.IsNullOrWhiteSpace(v)))
            .Select(r => new ImportTrack(Cell(r, title), Cell(r, artist), Cell(r, album), Cell(r, location))).ToList();
    }

    private static List<string[]> ParseDelimited(string text, char delimiter)
    {
        var rows = new List<string[]>(); var row = new List<string>(); var cell = new StringBuilder(); var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"' && quoted && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
            else if (c == '"') quoted = !quoted;
            else if (c == delimiter && !quoted) { row.Add(cell.ToString()); cell.Clear(); }
            else if ((c == '\n' || c == '\r') && !quoted)
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString()); cell.Clear(); rows.Add(row.ToArray()); row.Clear();
            }
            else cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row.ToArray()); }
        return rows;
    }

    private async Task<List<ImportJob>> LoadJobsAsync(CancellationToken ct)
    {
        if (_jobsFile is null || !File.Exists(_jobsFile)) return [];
        await using var input = File.OpenRead(_jobsFile);
        return await JsonSerializer.DeserializeAsync<List<ImportJob>>(input, _json, ct).ConfigureAwait(false) ?? [];
    }

    private async Task SaveJobAsync(ImportJob job, bool keep, CancellationToken ct)
    {
        if (_jobsFile is null) return;
        await _jobsGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var jobs = await LoadJobsAsync(ct).ConfigureAwait(false);
            jobs.RemoveAll(j => j.UserId == job.UserId && string.Equals(j.PlaylistName, job.PlaylistName, StringComparison.OrdinalIgnoreCase));
            if (keep) jobs.Add(job);
            await WriteJobsAsync(jobs, ct).ConfigureAwait(false);
        }
        finally { _jobsGate.Release(); }
    }

    private async Task SaveJobsAsync(List<ImportJob> jobs, CancellationToken ct)
    {
        if (_jobsFile is null) return;
        await _jobsGate.WaitAsync(ct).ConfigureAwait(false);
        try { await WriteJobsAsync(jobs, ct).ConfigureAwait(false); }
        finally { _jobsGate.Release(); }
    }

    private async Task WriteJobsAsync(List<ImportJob> jobs, CancellationToken ct)
    {
        var jobsFile = _jobsFile!;
        var temp = jobsFile + ".tmp";
        await using (var output = File.Create(temp))
            await JsonSerializer.SerializeAsync(output, jobs, _json, ct).ConfigureAwait(false);
        File.Move(temp, jobsFile, true);
    }

    private static string CleanPlaylistName(string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 120 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Playlist name must be 1-120 valid filename characters.");
        return name;
    }

    private static string Key(string? title, string? artist, string? album) => $"{Normalize(title)}|{Normalize(artist)}|{Normalize(album)}";
    private static string NormalizeHeader(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static string NormalizePath(string? value) => Path.GetFileName((value ?? string.Empty).Replace('\\', '/')).Normalize(NormalizationForm.FormKC).ToUpperInvariant();
    private static string Normalize(string? value) => new((value ?? string.Empty).Normalize(NormalizationForm.FormKC)
        .Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static string AlbumKey(ImportTrack track) => AlbumKey(track.Artist!, track.Album!);
    private static string AlbumKey(string artist, string album) => $"{Normalize(artist)}|{Normalize(album)}";

    public sealed class ImportJob
    {
        public Guid UserId { get; set; }
        public string PlaylistName { get; set; } = "";
        public List<ImportTrack> Tracks { get; set; } = [];
        public bool RequestMissingAlbums { get; set; }
        public HashSet<string> RequestedAlbums { get; set; } = [];
        public Dictionary<string, int> AlbumAttempts { get; set; } = [];
    }
    public sealed record ImportTrack(string? Title, string? Artist, string? Album, string? Path);
    private sealed record TrackMatch(ImportTrack Source, Audio? Item, bool Ambiguous);
    public sealed record MissingTrack(string? Title, string? Artist, string? Album, bool Ambiguous);
    public sealed record MissingAlbum(string Artist, string Album);
    public sealed record ImportPreview(string PlaylistName, int Total, int Matched, IReadOnlyList<MissingTrack> Missing, IReadOnlyList<MissingAlbum> MissingAlbums);
    public sealed record AlbumRequest(string Artist, string Album, bool Success, bool Retryable, string Message);
    public sealed record ImportResult(Guid PlaylistId, ImportPreview Preview,
        IReadOnlyList<AlbumRequest> AlbumRequests, int PendingAlbums,
        IReadOnlyList<MissingTrack> QueuedTracks, IReadOnlyList<MissingTrack> ManualTracks);
}
