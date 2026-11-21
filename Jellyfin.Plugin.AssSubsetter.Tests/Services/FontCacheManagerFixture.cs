using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;
using Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;
using Jellyfin.Plugin.AssSubsetter.Models;
using Jellyfin.Plugin.AssSubsetter.Services;
using Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Persistence;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Services;

internal sealed class FontCacheManagerFixture : IDisposable
{
    private readonly FontInventoryScanner _scanner;

    public FontCacheManagerFixture(IFontRepository? repository = null, FontMaintenanceCoordinator? coordinator = null)
    {
        repository ??= new InMemoryFontRepository();
        _scanner = new FontInventoryScanner(
            repository,
            () => new FontDirectoryPolicyResolver(null, null, null, includePlatformDefaults: false),
            new PathPhysicalFileIdentityProvider(),
            NullLogger<FontInventoryScanner>.Instance);
        Manager = new FontCacheManager(repository, _scanner, coordinator ?? new FontMaintenanceCoordinator());
    }

    public FontCacheManager Manager { get; }

    public void Dispose()
    {
        Manager.Dispose();
        _scanner.Dispose();
    }

    public static async Task<(string Path, int FaceIndex)?> LookupAsync(FontCacheManager manager, FontDescriptor descriptor)
    {
        await using FontFileLease? lease = await manager.AcquireFontAsync(descriptor, [], TestContext.Current.CancellationToken);
        return lease is null ? null : (lease.Path, lease.FaceIndex);
    }

    public static FontRuntimeSnapshotStartupService CreateStartupService(
        FontCacheManager manager,
        IFontRepository repository,
        FileRenameManifestStore renameManifests,
        IQuarantineManifestStore quarantineManifests,
        FontMaintenanceCoordinator coordinator,
        FontCatalogReadiness readiness,
        IServerApplicationHost? applicationHost = null,
        IFontFilenameNormalizationExecutor? normalizer = null)
        => new(manager, NullLogger<FontRuntimeSnapshotStartupService>.Instance, repository, renameManifests, quarantineManifests,
            coordinator, applicationHost ?? Mock.Of<IServerApplicationHost>(host => host.CoreStartupHasCompleted),
            normalizer ?? Mock.Of<IFontFilenameNormalizationExecutor>(), readiness);
}
