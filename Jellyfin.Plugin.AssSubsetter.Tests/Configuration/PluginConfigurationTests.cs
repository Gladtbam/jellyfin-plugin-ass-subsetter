using Jellyfin.Plugin.AssSubsetter.Configuration;
using Xunit;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Configuration;

public class PluginConfigurationTests
{
    [Fact]
    public void Constructor_ShouldSetDefaultValues()
    {
        // Arrange & Act
        var config = new PluginConfiguration();

        // Assert
        Assert.True(config.EnableAutoScanProcessing);
        Assert.Equal(1024, config.MaxCacheSizeMB);
        Assert.Equal(string.Empty, config.FontCacheDirectory);
        Assert.True(config.FallbackToOriginalOnError);
    }
}
