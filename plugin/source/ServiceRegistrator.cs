using JellyMusicDiscovery.Filters;
using JellyMusicDiscovery.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace JellyMusicDiscovery;

public class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
    {
        // Stash the server's SystemId so the response-augmenter can stamp it on
        // synthetic BaseItemDtos (the web client navigates to a broken state if
        // ServerId is undefined).
        Plugin.ServerSystemId = host.SystemId;

        services.AddHttpClient();

        services.AddSingleton<MusicBrainzClient>();
        services.AddSingleton<DeezerClient>();
        services.AddSingleton<ItunesClient>();
        services.AddSingleton<RadioBrowserClient>();
        services.AddSingleton<CoverArtArchiveClient>();
        services.AddSingleton<LidarrClient>();
        services.AddSingleton<SlskdClient>();
        services.AddSingleton<SlskdDownloadFinisher>();
        services.AddSingleton<LrcLibClient>();
        services.AddSingleton<LibraryRegistrar>();
        services.AddSingleton<RecentlyPlayedTracker>();
        services.AddSingleton<PlaylistStubLinks>();
        services.AddSingleton<DiscoveryPlaylistFeed>();
        services.AddSingleton<DiscoveryManager>();
        services.AddSingleton<StreamSessionRegistry>();
        services.AddSingleton<DiscoveryItemCache>();
        services.AddSingleton<PlaylistImportService>();
        services.AddSingleton<IMediaSourceProvider, DiscoveryMediaSourceProvider>();

        services.Configure<MvcOptions>(opts =>
        {
            opts.Filters.Add<MusicSearchActionFilter>();
            opts.Filters.Add<MusicItemDetailActionFilter>();
            opts.Filters.Add<MusicImageActionFilter>();
            opts.Filters.Add<MusicPlaybackInfoFilter>();
        });

        services.AddScoped<MusicSearchActionFilter>();
        services.AddScoped<MusicItemDetailActionFilter>();
        services.AddScoped<MusicImageActionFilter>();
        services.AddScoped<MusicPlaybackInfoFilter>();
    }
}
