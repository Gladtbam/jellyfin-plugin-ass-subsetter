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

internal static class FontCacheManagerFixture
{
    public static FontCacheManager CreateManager(IFontRepository? repository = null, FontMaintenanceCoordinator? coordinator = null)
    {
        repository ??= new InMemoryFontRepository();
        var scanner = new FontInventoryScanner(
            repository,
            () => new FontDirectoryPolicyResolver(null, null, null, includePlatformDefaults: false),
            new PathPhysicalFileIdentityProvider(),
            NullLogger<FontInventoryScanner>.Instance);
        return new FontCacheManager(repository, scanner, coordinator ?? new FontMaintenanceCoordinator());
    }

    public static async Task<(string Path, int FaceIndex)?> LookupAsync(FontCacheManager manager, FontDescriptor descriptor)
    {
        await using FontFileLease? lease = await manager.AcquireFontAsync(descriptor, Array.Empty<uint>(), TestContext.Current.CancellationToken);
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
