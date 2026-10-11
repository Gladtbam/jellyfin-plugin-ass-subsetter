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
        Assert.Equal(3, config.FontHistoryRetentionCount);
        Assert.Equal(string.Empty, config.CustomFontDirectories);
        Assert.Equal(string.Empty, config.ProtectedFontDirectories);
        Assert.Equal(string.Empty, config.PreferredFontDirectories);
        Assert.True(config.FallbackToOriginalOnError);
    }

    [Fact]
    public void GenerateMks_ShouldUseStableValueTwo()
    {
        Assert.Equal(2, (int)SubtitleProcessingMode.GenerateMks);
    }
}
