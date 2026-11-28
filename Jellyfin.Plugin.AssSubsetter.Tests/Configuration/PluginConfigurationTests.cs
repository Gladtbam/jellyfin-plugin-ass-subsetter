using System.Xml.Serialization;
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
        Assert.Equal(2, config.FontDeepAnalysisConcurrency);
        Assert.Equal(string.Empty, config.CustomFontDirectories);
        Assert.Equal(string.Empty, config.ProtectedFontDirectories);
        Assert.Equal(string.Empty, config.PreferredFontDirectories);
        Assert.True(config.FallbackToOriginalOnError);
    }

    [Fact]
    public void DeserializeLegacySettings_ShouldDefaultToScheduledAnalysisOnly()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var reader = new StringReader("<PluginConfiguration><FontHistoryRetentionCount>5</FontHistoryRetentionCount></PluginConfiguration>");

        var config = Assert.IsType<PluginConfiguration>(serializer.Deserialize(reader));

        Assert.True(config.EnableScheduledFontAnalysis);
        Assert.False(config.EnableScheduledFontQuarantine);
        Assert.False(config.EnableScheduledFontRenaming);
        Assert.Equal(2, config.FontDeepAnalysisConcurrency);
    }

    [Fact]
    public void SerializeSettings_ShouldPreserveConfiguredAnalysisConcurrency()
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));
        using var writer = new StringWriter();
        serializer.Serialize(writer, new PluginConfiguration { FontDeepAnalysisConcurrency = 8 });
        using var reader = new StringReader(writer.ToString());

        var config = Assert.IsType<PluginConfiguration>(serializer.Deserialize(reader));

        Assert.Equal(8, config.FontDeepAnalysisConcurrency);
    }

    [Fact]
    public void GenerateMks_ShouldUseStableValueTwo()
    {
        Assert.Equal(2, (int)SubtitleProcessingMode.GenerateMks);
    }
}
