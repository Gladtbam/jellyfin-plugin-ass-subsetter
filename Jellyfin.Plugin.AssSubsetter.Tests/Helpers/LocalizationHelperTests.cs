using Jellyfin.Plugin.AssSubsetter.Helpers;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Helpers;

public class LocalizationHelperTests
{
    [Theory]
    [InlineData("zh-CN", "ASS Subsetter 字体维护", "字体文件重命名", "压缩数据库并优化可用空间")]
    [InlineData("en-US", "ASS Subsetter font maintenance", "filename renaming", "compacts the database and optimizes free space")]
    [InlineData(null, "ASS Subsetter font maintenance", "filename renaming", "compacts the database and optimizes free space")]
    public void Initialize_LocalizesFontMaintenanceTask(string? uiCulture, string name, string renamingDescription, string databaseDescription)
    {
        LocalizationHelper.Initialize(uiCulture);

        Assert.Equal(name, LocalizationHelper.GetString("FontMaintenanceTask_Name"));
        string description = LocalizationHelper.GetString("FontMaintenanceTask_Description");
        Assert.Contains(renamingDescription, description, StringComparison.Ordinal);
        Assert.Contains(databaseDescription, description, StringComparison.Ordinal);
    }
}
