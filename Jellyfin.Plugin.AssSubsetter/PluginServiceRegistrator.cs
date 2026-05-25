using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.Middleware;
using Jellyfin.Plugin.AssSubsetter.ScheduledTasks;
using Jellyfin.Plugin.AssSubsetter.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter;

/// <summary>
/// Registers plugin services and injects the interceptor middleware into the ASP.NET Core pipeline.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton(provider => Plugin.Instance!.Configuration);

        serviceCollection.AddSingleton<ToolManager>();

        serviceCollection.AddSingleton(provider =>
        {
            var toolManager = provider.GetRequiredService<ToolManager>();
            var logger = provider.GetRequiredService<ILogger<AssProcessor>>();
            var config = provider.GetRequiredService<PluginConfiguration>();
            return new AssProcessor(toolManager, config, logger);
        });

        serviceCollection.AddSingleton(provider =>
        {
            var assProcessor = provider.GetRequiredService<AssProcessor>();
            var logger = provider.GetRequiredService<ILogger<SubtitleCacheService>>();
            var config = provider.GetRequiredService<PluginConfiguration>();
            return new SubtitleCacheService(config, assProcessor, string.Empty, logger);
        });

        serviceCollection.AddHostedService<LibraryScanTracker>();

        serviceCollection.AddTransient<IStartupFilter, SubtitleInterceptorStartupFilter>();
    }
}
