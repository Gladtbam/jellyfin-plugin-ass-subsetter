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
        Assert.Contains("分析重复字体", LocalizationHelper.GetString("FontAnalysisTask_Name"));
        Assert.Contains("扫描字体目录", LocalizationHelper.GetString("FontAnalysisTask_Description"));
        Assert.Contains("隔离", LocalizationHelper.GetString("FontQuarantineTask_Description"));
        Assert.DoesNotContain("24", LocalizationHelper.GetString("FontAnalysisTask_Description"));
        Assert.DoesNotContain("24", LocalizationHelper.GetString("FontQuarantineTask_Description"));
        Assert.DoesNotContain("24", LocalizationHelper.GetString("FontPurgeTask_Description"));
        Assert.DoesNotContain("计划", LocalizationHelper.GetString("FontAnalysisTask_Description"));
        Assert.DoesNotContain("计划", LocalizationHelper.GetString("FontQuarantineTask_Description"));
        Assert.DoesNotContain("计划", LocalizationHelper.GetString("FontPurgeTask_Description"));
        Assert.Contains("30 天", LocalizationHelper.GetString("FontPurgeTask_Description"));
        Assert.Contains("当前命名规则", LocalizationHelper.GetString("FontNormalizationTask_Description"));
        Assert.Contains("排队中的重命名或回滚请求", LocalizationHelper.GetString("FontNormalizationTask_Description"));
    }

    [Fact]
    public void Initialize_WithEnCulture_ReturnsEnglish()
    {
        // Act
        LocalizationHelper.Initialize("en-US");

        // Assert
        Assert.Equal("en-US", LocalizationHelper.Culture.Name);
        Assert.Contains("Build", LocalizationHelper.GetString("FontCacheTask_Name"));
        Assert.Contains("analyze duplicates", LocalizationHelper.GetString("FontAnalysisTask_Name"));
        Assert.Contains("Scan font directories", LocalizationHelper.GetString("FontAnalysisTask_Description"));
        Assert.Contains("analysis report", LocalizationHelper.GetString("FontAnalysisTask_Description"));
        Assert.Contains("Quarantine", LocalizationHelper.GetString("FontQuarantineTask_Description"));
        Assert.DoesNotContain("24", LocalizationHelper.GetString("FontAnalysisTask_Description"));
        Assert.DoesNotContain("24", LocalizationHelper.GetString("FontQuarantineTask_Description"));
        Assert.DoesNotContain("24", LocalizationHelper.GetString("FontPurgeTask_Description"));
        Assert.DoesNotContain("schedule", LocalizationHelper.GetString("FontAnalysisTask_Description"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("schedule", LocalizationHelper.GetString("FontQuarantineTask_Description"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("schedule", LocalizationHelper.GetString("FontPurgeTask_Description"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("at least 30 days", LocalizationHelper.GetString("FontPurgeTask_Description"));
        Assert.Contains("current naming rules", LocalizationHelper.GetString("FontNormalizationTask_Description"));
        Assert.Contains("queued rename or rollback requests", LocalizationHelper.GetString("FontNormalizationTask_Description"));
        Assert.DoesNotContain("default trigger", LocalizationHelper.GetString("FontNormalizationTask_Description"));
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
