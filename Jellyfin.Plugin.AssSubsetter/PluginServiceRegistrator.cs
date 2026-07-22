using System;
using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.Core;
using Jellyfin.Plugin.AssSubsetter.Middleware;
using Jellyfin.Plugin.AssSubsetter.Native;
using Jellyfin.Plugin.AssSubsetter.ScheduledTasks;
using Jellyfin.Plugin.AssSubsetter.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter;

/// <summary>
///     Registers plugin services and injects the interceptor middleware into the ASP.NET Core pipeline.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Register libass native library resolver for ASS to SUP conversion
        LibassNative.RegisterResolver();

        serviceCollection.AddSingleton<Func<PluginConfiguration>>(() => Plugin.Instance!.Configuration);

        serviceCollection.AddSingleton<FontCacheManager>();
        serviceCollection.AddSingleton<AssDocumentParser>();

        serviceCollection.AddSingleton(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<AssProcessor>>();
            var configFactory = provider.GetRequiredService<Func<PluginConfiguration>>();
            var fontCacheManager = provider.GetRequiredService<FontCacheManager>();
            var assParser = provider.GetRequiredService<AssDocumentParser>();
            return new AssProcessor(configFactory, fontCacheManager, assParser, logger);
        });

        serviceCollection.AddSingleton(provider =>
        {
            var configFactory = provider.GetRequiredService<Func<PluginConfiguration>>();
            var logger = provider.GetRequiredService<ILogger<AssToSupConverter>>();
            return new AssToSupConverter(configFactory, logger);
        });

        serviceCollection.AddSingleton<MksMuxer>();
        serviceCollection.AddSingleton<SubtitleStreamResolver>();

        serviceCollection.AddSingleton(provider =>
        {
            var assProcessor = provider.GetRequiredService<AssProcessor>();
            var assToSupConverter = provider.GetRequiredService<AssToSupConverter>();
            var mksMuxer = provider.GetRequiredService<MksMuxer>();
            var logger = provider.GetRequiredService<ILogger<SubtitleCacheService>>();
            var configFactory = provider.GetRequiredService<Func<PluginConfiguration>>();
            return new SubtitleCacheService(configFactory, assProcessor, assToSupConverter, mksMuxer, string.Empty, logger);
        });
        serviceCollection.AddSingleton<IHostedService>(provider => provider.GetRequiredService<SubtitleCacheService>());

        serviceCollection.AddHostedService<LibraryScanTracker>();

        serviceCollection.AddHostedService<PlaybackPrefetchService>();

        serviceCollection.AddTransient<IStartupFilter, SubtitleInterceptorStartupFilter>();
    }
}
