using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;
using Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;
using MediaBrowser.Controller;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.Services;

/// <summary>Recovers the font catalog and loads the last published snapshot after Jellyfin starts.</summary>
public sealed class FontRuntimeSnapshotStartupService : BackgroundService
{
    private static readonly TimeSpan _startupCheckInterval = TimeSpan.FromMilliseconds(250);
    private readonly FontCacheManager _fontCacheManager;
    private readonly ILogger<FontRuntimeSnapshotStartupService> _logger;
    private readonly IFontInventoryReader _repository;
    private readonly FileRenameManifestStore _renameManifests;
    private readonly IQuarantineManifestStore _quarantineManifests;
    private readonly FontMaintenanceCoordinator _coordinator;
    private readonly IServerApplicationHost _applicationHost;
    private readonly IFontFilenameNormalizationExecutor _normalizer;
    private readonly FontCatalogReadiness _readiness;
    private readonly PlatformPhysicalFileIdentityProvider _identity = new();

    /// <summary>Initializes a new instance of the <see cref="FontRuntimeSnapshotStartupService"/> class with both durable maintenance journals.</summary>
    /// <param name="fontCacheManager">The owner of the published immutable runtime snapshot.</param>
    /// <param name="logger">The diagnostic sink for startup failures.</param>
    /// <param name="repository">The durable catalog whose published generation may still contain moved paths.</param>
    /// <param name="renameManifests">The durable rename transition journal.</param>
    /// <param name="quarantineManifests">The durable quarantine recovery journal.</param>
    /// <param name="coordinator">The memory-only path availability fence.</param>
    /// <param name="applicationHost">Indicates when Jellyfin has completed its core startup tasks.</param>
    /// <param name="normalizer">Recovers durable rename and quarantine state before new maintenance work is allowed.</param>
    /// <param name="readiness">Reports the result of catalog recovery to other font consumers.</param>
    public FontRuntimeSnapshotStartupService(
        FontCacheManager fontCacheManager,
        ILogger<FontRuntimeSnapshotStartupService> logger,
        IFontInventoryReader repository,
        FileRenameManifestStore renameManifests,
        IQuarantineManifestStore quarantineManifests,
        FontMaintenanceCoordinator coordinator,
        IServerApplicationHost applicationHost,
        IFontFilenameNormalizationExecutor normalizer,
        FontCatalogReadiness readiness)
    {
        _fontCacheManager = fontCacheManager ?? throw new ArgumentNullException(nameof(fontCacheManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _renameManifests = renameManifests ?? throw new ArgumentNullException(nameof(renameManifests));
        _quarantineManifests = quarantineManifests ?? throw new ArgumentNullException(nameof(quarantineManifests));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _applicationHost = applicationHost;
        _normalizer = normalizer;
        _readiness = readiness;
    }

    /// <summary>
    /// Waits for Jellyfin's core startup to finish, then initializes the catalog, reconciles maintenance journals, and loads the published font snapshot.
    /// </summary>
    /// <param name="stoppingToken">Cancels the wait for Jellyfin or the catalog work in progress.</param>
    /// <returns>The background task that reports catalog readiness after the snapshot load attempt.</returns>
    /// <remarks>Logs catalog or snapshot failures. If snapshot loading fails after catalog recovery, font maintenance can still run.</remarks>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        bool recovered = false;
        try
        {
            // Jellyfin runs core startup tasks after the generic host has started.
            while (!_applicationHost.CoreStartupHasCompleted)
            {
                await Task.Delay(_startupCheckInterval, stoppingToken).ConfigureAwait(false);
            }

            stoppingToken.ThrowIfCancellationRequested();
            await _fontCacheManager.LoadPublishedSnapshotAsync(
                async token =>
                {
                    await _normalizer.ReconcileAsync(token).ConfigureAwait(false);
                    recovered = true;
                    await RestoreFencesAsync(token).ConfigureAwait(false);
                },
                stoppingToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(
                exception,
                "[AssSubsetter] Failed to {Operation}.",
                recovered ? "load the published font runtime snapshot" : "initialize or recover the font catalog");
        }
        finally
        {
            _readiness.Complete(recovered && !stoppingToken.IsCancellationRequested);
        }
    }

    private async Task RestoreFencesAsync(CancellationToken cancellationToken)
    {
        HashSet<string> paths = GetManifestPaths(
            await _renameManifests.LoadAllAsync(cancellationToken).ConfigureAwait(false),
            await _quarantineManifests.LoadAllAsync(cancellationToken).ConfigureAwait(false));
        while (paths.Count > 0)
        {
            IAsyncDisposable lease = await _coordinator.AcquireExclusiveAsync(paths, cancellationToken).ConfigureAwait(false);
            await using var leaseScope = lease.ConfigureAwait(false);
            var renameManifests = await _renameManifests.LoadAllAsync(cancellationToken).ConfigureAwait(false);
            var quarantineManifests = await _quarantineManifests.LoadAllAsync(cancellationToken).ConfigureAwait(false);
            HashSet<string> currentPaths = GetManifestPaths(renameManifests, quarantineManifests);
            if (!paths.IsSupersetOf(currentPaths))
            {
                // Reacquire the full path set in the coordinator's lock order if the journals changed.
                paths = currentPaths;
                continue;
            }

            var published = await _repository.LoadPublishedFileEvidenceByPathAsync(currentPaths, cancellationToken).ConfigureAwait(false);
            RestoreFences(renameManifests, quarantineManifests, published.ToLookup(file => file.Path, FontPathComparison.Comparer));
            return;
        }
    }

    private static HashSet<string> GetManifestPaths(
        IReadOnlyList<RenameManifest> renameManifests, IReadOnlyList<QuarantineManifest> quarantineManifests)
        => renameManifests.SelectMany(manifest => manifest.Batch.RenameItems.Select(item => manifest.Request.Kind == FontOperationKind.RollbackNames ? item.NewPath : item.OldPath))
            .Concat(quarantineManifests.SelectMany(manifest => manifest.Items.Select(item => item.SourcePath)))
            .ToHashSet(FontPathComparison.Comparer);

    private void RestoreFences(
        IReadOnlyList<RenameManifest> renameManifests,
        IReadOnlyList<QuarantineManifest> quarantineManifests,
        ILookup<string, (string Path, string PhysicalIdentity, long GenerationId)> publishedByPath)
    {
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
}
