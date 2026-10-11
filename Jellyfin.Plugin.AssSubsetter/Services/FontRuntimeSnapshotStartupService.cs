using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;
using Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.Services;

/// <summary>Loads the last published font snapshot during application startup without involving subtitle requests.</summary>
public sealed class FontRuntimeSnapshotStartupService : IHostedService
{
    private readonly FontCacheManager _fontCacheManager;
    private readonly ILogger<FontRuntimeSnapshotStartupService> _logger;
    private readonly IFontRepository _repository;
    private readonly FileRenameManifestStore _renameManifests;
    private readonly IQuarantineManifestStore _quarantineManifests;
    private readonly FontMaintenanceCoordinator _coordinator;
    private readonly PlatformPhysicalFileIdentityProvider _identity = new();

    /// <summary>Initializes a new instance of the <see cref="FontRuntimeSnapshotStartupService"/> class with both durable maintenance journals.</summary>
    /// <param name="fontCacheManager">The owner of the published immutable runtime snapshot.</param>
    /// <param name="logger">The diagnostic sink for startup failures.</param>
    /// <param name="repository">The durable catalog whose published generation may still contain moved paths.</param>
    /// <param name="renameManifests">The durable rename transition journal.</param>
    /// <param name="quarantineManifests">The durable quarantine recovery journal.</param>
    /// <param name="coordinator">The memory-only path availability fence.</param>
    public FontRuntimeSnapshotStartupService(
        FontCacheManager fontCacheManager,
        ILogger<FontRuntimeSnapshotStartupService> logger,
        IFontRepository repository,
        FileRenameManifestStore renameManifests,
        IQuarantineManifestStore quarantineManifests,
        FontMaintenanceCoordinator coordinator)
    {
        _fontCacheManager = fontCacheManager ?? throw new ArgumentNullException(nameof(fontCacheManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _renameManifests = renameManifests ?? throw new ArgumentNullException(nameof(renameManifests));
        _quarantineManifests = quarantineManifests ?? throw new ArgumentNullException(nameof(quarantineManifests));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    }

    /// <summary>Loads and publishes the durable runtime snapshot before hosted startup completes.</summary>
    /// <param name="cancellationToken">A token that cancels application startup.</param>
    /// <returns>A task representing the startup load.</returns>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _fontCacheManager.LoadPublishedSnapshotAsync(
                RestoreFencesAsync,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(exception, "[AssSubsetter] Failed to load the published font runtime snapshot during startup.");
        }
    }

    private async Task RestoreFencesAsync(CancellationToken cancellationToken)
    {
        var published = await _repository.LoadPublishedInventoryAsync(cancellationToken).ConfigureAwait(false);
        var publishedByPath = published.ToLookup(
            file => file.Path,
            FontPathComparison.Comparer);
        var renameManifests = await _renameManifests.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        var quarantineManifests = await _quarantineManifests.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        string[] paths = renameManifests.SelectMany(manifest => manifest.Batch.RenameItems.Select(item => manifest.Request.Kind == FontOperationKind.RollbackNames ? item.NewPath : item.OldPath))
            .Concat(quarantineManifests.SelectMany(manifest => manifest.Items.Select(item => item.SourcePath)))
            .Distinct(FontPathComparison.Comparer)
            .ToArray();
        IAsyncDisposable lease = await _coordinator.AcquireExclusiveAsync(paths, cancellationToken).ConfigureAwait(false);
        await using var leaseScope = lease.ConfigureAwait(false);
        renameManifests = await _renameManifests.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        quarantineManifests = await _quarantineManifests.LoadAllAsync(cancellationToken).ConfigureAwait(false);

        foreach (RenameManifest manifest in renameManifests)
        {
            bool rollback = manifest.Request.Kind == FontOperationKind.RollbackNames;
            foreach (RenameItemRecord item in manifest.Batch.RenameItems)
            {
                if (item.Status is not (RenameItemStatus.Moving or RenameItemStatus.Temporary
                    or RenameItemStatus.Renamed or RenameItemStatus.RollingBack or RenameItemStatus.RolledBack))
                {
                    continue;
                }

                string source = rollback ? item.NewPath : item.OldPath;
                if (publishedByPath[source].Any(file => string.Equals(file.PhysicalIdentity, item.Evidence.PhysicalIdentity, StringComparison.Ordinal)
                    && (rollback || file.GenerationId == item.Evidence.GenerationId))
                    && !OriginalFilePresent(source, item.Evidence.PhysicalIdentity))
                {
                    _coordinator.SetRenamePathAvailable(source, false);
                }
            }
        }

        foreach (QuarantineManifest manifest in quarantineManifests)
        {
            foreach (QuarantineManifestItem item in manifest.Items)
            {
                if (publishedByPath[item.SourcePath].Any(file => string.Equals(file.PhysicalIdentity, item.SourcePhysicalIdentity, StringComparison.Ordinal))
                    && !OriginalFilePresent(item.SourcePath, item.SourcePhysicalIdentity))
                {
                    _coordinator.SetPathAvailable(item.SourcePath, false);
                }
            }
        }
    }

    private bool OriginalFilePresent(string path, string identity)
    {
        try
        {
            return File.Exists(path) && string.Equals(_identity.GetIdentity(path), identity, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Stops the startup-only service without additional work.</summary>
    /// <param name="cancellationToken">The application shutdown token.</param>
    /// <returns>A completed task.</returns>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
