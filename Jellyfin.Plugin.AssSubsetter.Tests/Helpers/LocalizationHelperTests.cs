using Jellyfin.Plugin.AssSubsetter.Helpers;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Helpers;

public class LocalizationHelperTests
{
    [Fact]
    public void Initialize_WithZhCulture_ReturnsChinese()
    {
        // Act
        LocalizationHelper.Initialize("zh-CN");

        // Assert
        Assert.Equal("zh-CN", LocalizationHelper.Culture.Name);
        Assert.Contains("构建", LocalizationHelper.GetString("FontCacheTask_Name"));
    }

    [Fact]
    public void Initialize_WithEnCulture_ReturnsEnglish()
    {
        // Act
        LocalizationHelper.Initialize("en-US");

        // Assert
        Assert.Equal("en-US", LocalizationHelper.Culture.Name);
        Assert.Contains("Build", LocalizationHelper.GetString("FontCacheTask_Name"));
    }

    [Fact]
    public void Initialize_WithNullCulture_FallsBackToEnglish()
    {
        // Act
        LocalizationHelper.Initialize(null);

        // Assert
        Assert.Equal("en", LocalizationHelper.Culture.Name);
        Assert.Contains("Build", LocalizationHelper.GetString("FontCacheTask_Name"));
    }
}
