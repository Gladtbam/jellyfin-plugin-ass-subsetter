using System;
using System.IO;
using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.Core;
using Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;
using Jellyfin.Plugin.AssSubsetter.Fonts.Api;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;
using Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;
using Jellyfin.Plugin.AssSubsetter.Middleware;
using Jellyfin.Plugin.AssSubsetter.Native;
using Jellyfin.Plugin.AssSubsetter.ScheduledTasks;
using Jellyfin.Plugin.AssSubsetter.Services;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Jellyfin.Plugin.AssSubsetter;

/// <summary>
///     Registers plugin services and injects the interceptor middleware into the ASP.NET Core pipeline.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <summary>Registers the plugin's singleton services, persistent font catalog, and hosted integrations.</summary>
    /// <param name="serviceCollection">The Jellyfin dependency-injection service collection.</param>
    /// <param name="applicationHost">The active Jellyfin server application host.</param>
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Register libass native library resolver for ASS to SUP conversion
        LibassNative.RegisterResolver();

        serviceCollection.AddSingleton<Func<PluginConfiguration>>(() => Plugin.Instance!.Configuration);
        serviceCollection.AddSingleton(_ => Plugin.Instance!.StoragePaths);
        serviceCollection.AddSingleton(TimeProvider.System);

        serviceCollection.AddSingleton<FontDatabase>();
        serviceCollection.AddSingleton<IFontRepository, SqliteFontRepository>();
        // Capability aliases share the aggregate repository and its atomic publication boundary.
        serviceCollection.AddSingleton<IFontRepositoryLifecycle>(provider => provider.GetRequiredService<IFontRepository>());
        serviceCollection.AddSingleton<IFontInventoryReader>(provider => provider.GetRequiredService<IFontRepository>());
        serviceCollection.AddSingleton<IFontInventoryWriter>(provider => provider.GetRequiredService<IFontRepository>());
        serviceCollection.AddSingleton<IFontRuntimeFaceReader>(provider => provider.GetRequiredService<IFontRepository>());
        serviceCollection.AddSingleton<IFontAnalysisReader>(provider => provider.GetRequiredService<IFontRepository>());
        serviceCollection.AddSingleton<IFontAnalysisPublisher>(provider => provider.GetRequiredService<IFontRepository>());
        serviceCollection.AddSingleton<IFontAnalysisEvidenceRepository>(provider => provider.GetRequiredService<IFontRepository>());
        serviceCollection.AddSingleton<IFontRetentionRepository>(provider => provider.GetRequiredService<IFontRepository>());
        serviceCollection.AddSingleton<IFontOperationReader>(provider => provider.GetRequiredService<IFontRepository>());
        serviceCollection.AddSingleton<IFontOperationRepository>(provider => provider.GetRequiredService<IFontRepository>());
        serviceCollection.AddSingleton<IFontMaintenanceReader>(provider => provider.GetRequiredService<IFontRepository>());
        serviceCollection.AddSingleton<IFontMaintenanceRepository>(provider => provider.GetRequiredService<IFontRepository>());
        serviceCollection.AddSingleton<IFontDiagnosticsReader>(provider => provider.GetRequiredService<IFontRepository>());
        serviceCollection.AddSingleton<IFontDiagnosticsWriter>(provider => provider.GetRequiredService<IFontRepository>());
        serviceCollection.AddSingleton<IFontRenameReader>(provider => provider.GetRequiredService<IFontRepository>());
        serviceCollection.AddSingleton<IFontRenameRepository>(provider => provider.GetRequiredService<IFontRepository>());
        serviceCollection.AddSingleton<IFontManagementReader>(provider => provider.GetRequiredService<IFontRepository>());
        serviceCollection.AddSingleton<IFontMaintenanceExecutionRepository>(provider => provider.GetRequiredService<IFontRepository>());
        serviceCollection.AddSingleton<IPhysicalFileIdentityProvider, PlatformPhysicalFileIdentityProvider>();
        serviceCollection.AddSingleton<FontMaintenanceCoordinator>();
        serviceCollection.AddSingleton(provider =>
        {
            var configFactory = provider.GetRequiredService<Func<PluginConfiguration>>();
            return new Func<FontDirectoryPolicyResolver>(() =>
            {
                PluginConfiguration config = configFactory();
                return new FontDirectoryPolicyResolver(
                    config.CustomFontDirectories,
                    config.ProtectedFontDirectories,
                    config.PreferredFontDirectories);
            });
        });
        serviceCollection.AddSingleton<FontInventoryScanner>();
        serviceCollection.AddSingleton<IFontAnalysisEvidenceSource, PhysicalFontAnalysisEvidenceSource>();
        serviceCollection.AddSingleton<FontRelationshipClassifier>();
        serviceCollection.AddSingleton<FontRetentionSelector>();
        serviceCollection.AddSingleton<FontDuplicateAnalyzer>();
        serviceCollection.AddSingleton<FontOperationRequestStore>();
        serviceCollection.AddSingleton<FontManagementQuery>();
        serviceCollection.AddSingleton<IQuarantineFileSystem, PhysicalQuarantineFileSystem>();
        serviceCollection.AddSingleton<IQuarantineManifestStore>(provider =>
        {
            FontStoragePaths paths = provider.GetRequiredService<FontStoragePaths>();
            return new FileQuarantineManifestStore(paths.ManifestPath, paths.QuarantinePath);
        });
        serviceCollection.AddSingleton(provider =>
        {
            FontStoragePaths paths = provider.GetRequiredService<FontStoragePaths>();
            return new FontQuarantineManager(
                provider.GetRequiredService<IFontRepository>(),
                provider.GetRequiredService<FontMaintenanceCoordinator>(),
                provider.GetRequiredService<IQuarantineFileSystem>(),
                provider.GetRequiredService<IQuarantineManifestStore>(),
                paths.QuarantinePath,
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<Func<FontDirectoryPolicyResolver>>(),
                provider.GetRequiredService<ILogger<FontQuarantineManager>>());
        });
        serviceCollection.AddSingleton(provider => new FileRenameManifestStore(Path.Combine(provider.GetRequiredService<FontStoragePaths>().ManifestPath, "rename")));
        serviceCollection.AddSingleton<IFontRenameDirectoryOperations, PhysicalFontRenameDirectoryOperations>();
        serviceCollection.AddSingleton(provider => new FontRenamePlanner(provider.GetRequiredService<IFontRenameDirectoryOperations>(), provider.GetRequiredService<FontMaintenanceCoordinator>().IsInUse));
        serviceCollection.AddSingleton<Func<FontNamingRules>>(provider => () =>
        {
            PluginConfiguration config = provider.GetRequiredService<Func<PluginConfiguration>>()();
            return new FontNamingRules(config.FontFileNameTemplate, config.FontFolderTemplate, config.RemoveEmptyFontDirectories);
        });
        serviceCollection.AddSingleton(provider => new FontRenameCandidateService(provider.GetRequiredService<IFontRepository>(), provider.GetRequiredService<FontRenamePlanner>(), provider.GetRequiredService<Func<FontNamingRules>>(), provider.GetRequiredService<Func<FontDirectoryPolicyResolver>>(), provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<IQuarantineFileSystem>(), provider.GetRequiredService<IFontRenameDirectoryOperations>(), provider.GetRequiredService<FontMaintenanceCoordinator>()));
        serviceCollection.AddSingleton<IFontFilenameNormalizationExecutor>(provider => new FontFilenameNormalizer(
            provider.GetRequiredService<IFontRepository>(),
            provider.GetRequiredService<FontMaintenanceCoordinator>(),
            provider.GetRequiredService<IQuarantineFileSystem>(),
            provider.GetRequiredService<FileRenameManifestStore>(),
            provider.GetRequiredService<Func<FontDirectoryPolicyResolver>>(),
            provider.GetRequiredService<IPhysicalFileIdentityProvider>(),
            provider.GetRequiredService<FontRenameCandidateService>(),
            provider.GetRequiredService<IFontRenameDirectoryOperations>(),
            async token => await provider.GetRequiredService<FontQuarantineManager>().ReconcileAsync(token).ConfigureAwait(false)));
        serviceCollection.AddHostedService(provider => new FontRenameRecoveryService(
            provider.GetRequiredService<IFontRepository>(),
            provider.GetRequiredService<IFontFilenameNormalizationExecutor>()));
        serviceCollection.AddSingleton(provider => new FontRenameCandidateRefreshQueue(
            provider.GetRequiredService<IFontRepository>(),
            provider.GetRequiredService<Func<FontNamingRules>>(),
            provider.GetRequiredService<FontRenameCandidateService>().ReadRequirements,
            provider.GetRequiredService<TimeProvider>()));
        serviceCollection.AddSingleton(provider => new FontRenameCandidateRefreshService(
            provider.GetRequiredService<FontRenameCandidateRefreshQueue>(),
            provider.GetRequiredService<IFontOperationRequestDispatcher>(),
            signal =>
            {
                Plugin plugin = Plugin.Instance!;
                EventHandler<MediaBrowser.Model.Plugins.BasePluginConfiguration> handler = (_, _) => signal();
                plugin.ConfigurationChanged += handler;
                return new ConfigurationSignalSubscription(() => plugin.ConfigurationChanged -= handler);
            },
            provider.GetRequiredService<Func<FontNamingRules>>(),
            provider.GetRequiredService<ILogger<FontRenameCandidateRefreshService>>()));
        serviceCollection.AddSingleton<IHostedService>(provider => provider.GetRequiredService<FontRenameCandidateRefreshService>());

        serviceCollection.AddSingleton<FontAnalysisWorkflow>();
        serviceCollection.AddSingleton<IFontMaintenanceTaskExecutor, FontMaintenanceTaskExecutor>();
        serviceCollection.AddSingleton(provider => new FontOperationRequestScheduler(
            provider.GetRequiredService<IFontRepository>(),
            provider.GetRequiredService<ITaskManager>(),
            () => provider.GetRequiredService<IFontMaintenanceTaskExecutor>().IsBusy,
            (kind, token) => provider.GetRequiredService<IFontMaintenanceTaskExecutor>().ExecutePendingOperationAsync(kind, new Progress<double>(), token),
            provider.GetRequiredService<ILogger<FontOperationRequestScheduler>>()));
        serviceCollection.AddSingleton<IFontOperationRequestDispatcher>(provider => provider.GetRequiredService<FontOperationRequestScheduler>());
        serviceCollection.AddSingleton<IHostedService>(provider => provider.GetRequiredService<FontOperationRequestScheduler>());
        serviceCollection.AddSingleton<FontRestoreRequestWorker>();
        serviceCollection.AddSingleton<IFontRestoreRequestDispatcher>(provider => provider.GetRequiredService<FontRestoreRequestWorker>());
        serviceCollection.AddSingleton<IHostedService>(provider => provider.GetRequiredService<FontRestoreRequestWorker>());

        serviceCollection.AddSingleton(provider => new FontCacheManager(
            provider.GetRequiredService<IFontRepository>(),
            provider.GetRequiredService<FontInventoryScanner>(),
            provider.GetRequiredService<FontMaintenanceCoordinator>()));
        serviceCollection.AddHostedService<FontRuntimeSnapshotStartupService>();
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
            return new AssToSupConverter(configFactory, logger, provider.GetRequiredService<AssProcessor>());
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
            return new SubtitleCacheService(configFactory, assProcessor, assToSupConverter, mksMuxer, string.Empty, logger, provider.GetRequiredService<FontCacheManager>());
        });
        serviceCollection.AddSingleton<IHostedService>(provider => provider.GetRequiredService<SubtitleCacheService>());

        serviceCollection.AddHostedService<LibraryScanTracker>();

        serviceCollection.AddHostedService<PlaybackPrefetchService>();

        serviceCollection.AddTransient<IConfigureOptions<MvcOptions>, SubtitleInterceptorStartupFilter>();
    }

    private sealed class ConfigurationSignalSubscription(Action unsubscribe) : IDisposable
    {
        private Action? _unsubscribe = unsubscribe;

        public void Dispose() => System.Threading.Interlocked.Exchange(ref _unsubscribe, null)?.Invoke();
    }
}
