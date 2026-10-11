using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;
using Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;
using Jellyfin.Plugin.AssSubsetter.Models;
using Jellyfin.Plugin.AssSubsetter.Services;
using Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

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
}
