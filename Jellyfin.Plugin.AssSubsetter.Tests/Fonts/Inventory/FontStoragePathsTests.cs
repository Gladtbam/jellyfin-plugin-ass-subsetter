using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Inventory;

public sealed class FontStoragePathsTests
{
    [Fact]
    public void Create_ShouldUseFixedPluginDataPaths()
    {
        var paths = FontStoragePaths.Create(Path.Join("jellyfin", "data"));

        Assert.Equal(Path.Join("jellyfin", "data", "ass-subsetter"), paths.RootPath);
        Assert.Equal(Path.Join(paths.RootPath, "ass-subsetter.db"), paths.DatabasePath);
        Assert.Equal(Path.Join(paths.RootPath, "font-quarantine"), paths.QuarantinePath);
        Assert.Equal(Path.Join(paths.RootPath, "manifests"), paths.ManifestPath);
    }
}
