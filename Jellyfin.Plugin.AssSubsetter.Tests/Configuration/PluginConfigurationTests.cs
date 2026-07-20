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
        Assert.Equal(SubtitleProcessingMode.Subsetting, config.SubtitleMode);
        Assert.Equal(1024, config.MaxCacheSizeMB);
        Assert.Equal(string.Empty, config.FontCacheFilePath);
        Assert.True(config.FallbackToOriginalOnError);
    }

    [Fact]
    public void GenerateMks_ShouldUseStableValueTwo()
    {
        Assert.Equal(2, (int)SubtitleProcessingMode.GenerateMks);
    }
}
