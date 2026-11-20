using Jellyfin.Plugin.AssSubsetter.Helpers;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Helpers;

public class LocalizationHelperTests
{
    [Theory]
    [InlineData("zh-CN", "ASS Subsetter 字体维护", "字体文件重命名")]
    [InlineData("en-US", "ASS Subsetter font maintenance", "filename renaming")]
    [InlineData(null, "ASS Subsetter font maintenance", "filename renaming")]
    public void Initialize_LocalizesFontMaintenanceTask(string? uiCulture, string name, string descriptionFragment)
    {
        LocalizationHelper.Initialize(uiCulture);

        Assert.Equal(name, LocalizationHelper.GetString("FontMaintenanceTask_Name"));
        Assert.Contains(descriptionFragment, LocalizationHelper.GetString("FontMaintenanceTask_Description"), StringComparison.Ordinal);
    }
}
