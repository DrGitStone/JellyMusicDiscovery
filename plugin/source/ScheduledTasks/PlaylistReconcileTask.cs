using JellyMusicDiscovery.Services;
using MediaBrowser.Model.Tasks;

namespace JellyMusicDiscovery.ScheduledTasks;

public sealed class PlaylistReconcileTask : IScheduledTask
{
    private readonly PlaylistImportService _imports;

    public PlaylistReconcileTask(PlaylistImportService imports) => _imports = imports;

    public string Name => "Music Discovery: reconcile imported playlists";
    public string Key => "MusicDiscoveryPlaylistReconcile";
    public string Description => "Queues missing albums and adds newly downloaded tracks to imported playlists.";
    public string Category => "Music Discovery";

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
    {
        await _imports.ReconcileAsync(ct).ConfigureAwait(false);
        progress.Report(100);
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() =>
    [
        new()
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromMinutes(15).Ticks,
        },
    ];
}
