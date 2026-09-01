using System;
using Jellyfin.Plugin.M3u8Downloader.Configuration;
using Jellyfin.Plugin.M3u8Downloader.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.M3u8Downloader;

/// <summary>
/// Registers the plugin's services with the server's DI container at startup.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Read through a delegate rather than touching Plugin.Instance at the point of use: the
        // configuration can be edited at any time, so it must not be captured, and the indirection
        // is what lets the worker and the controller be constructed in a test.
        serviceCollection.AddSingleton<Func<PluginConfiguration>>(
            () => Plugin.Instance?.Configuration ?? new PluginConfiguration());

        // The queue holds all state and must be shared by the worker and the API controller.
        serviceCollection.AddSingleton<DownloadQueueService>();
        serviceCollection.AddSingleton<IDownloadQueueService>(sp => sp.GetRequiredService<DownloadQueueService>());

        // Singleton so its reservation lock covers every download in the server: two jobs
        // resolving the same output name must not both find it free.
        serviceCollection.AddSingleton<OutputFilePublisher>();

        serviceCollection.AddSingleton<SourceProbe>();
        serviceCollection.AddSingleton<ISourceProbe>(sp => sp.GetRequiredService<SourceProbe>());

        serviceCollection.AddSingleton<FfmpegDownloader>();
        serviceCollection.AddSingleton<IFfmpegDownloader>(sp => sp.GetRequiredService<FfmpegDownloader>());

        // The main plugin class cannot itself be a hosted service, so the worker is its own type.
        serviceCollection.AddHostedService<QueueWorker>();
    }
}
