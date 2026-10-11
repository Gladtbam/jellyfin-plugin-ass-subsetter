using System;
using System.Globalization;

namespace Jellyfin.Plugin.AssSubsetter.Helpers;

/// <summary>
///     Provides localized string access based on the server's UICulture setting.
/// </summary>
public static class LocalizationHelper
{
    private static string _culture = "en";

    /// <summary>
    ///     Gets the current UI culture.
    /// </summary>
    public static CultureInfo Culture => new CultureInfo(_culture);

    /// <summary>
    ///     Initializes the localization helper with the server's UI culture.
    /// </summary>
    /// <param name="uiCulture">The UI culture string from server configuration (e.g. "zh-CN", "en-US").</param>
    public static void Initialize(string? uiCulture)
    {
        _culture = uiCulture ?? "en";
    }

    /// <summary>
    ///     Gets the localized string for a specific key.
    /// </summary>
    /// <param name="key">The translation key.</param>
    /// <returns>The localized string.</returns>
    public static string GetString(string key)
    {
        bool isZh = _culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        return key switch
        {
            "FontCacheTask_Name" => isZh ? "构建 ASS Subsetter 本地字体索引缓存" : "Build ASS Subsetter local font index cache",
            "FontCacheTask_Description" => isZh ? "扫描系统字体与配置的自定义字体目录。" : "Scan system fonts and configured custom font directories.",
            "FontAnalysisTask_Name" => isZh ? "更新字体库存并分析重复字体" : "Update font inventory and analyze duplicates",
            "FontAnalysisTask_Description" => isZh
                ? "扫描字体目录、发布库存并生成重复字体分析报告"
                : "Scan font directories, publish inventory, and generate a duplicate-font analysis report.",
            "FontAnalysisRetentionTask_Name" => isZh ? "清理旧分析批次与字体库存代次" : "Prune old analysis batches and published font inventory generations",
            "FontAnalysisRetentionTask_Description" => isZh
                ? "按常规设置的保留数量清理旧分析报告与库存代次；0 表示全部保留。保留当前发布结果及待处理或执行中请求所需的报告，并清理过期的未完成扫描。"
                : "Prune old analysis reports and inventory generations using the retention count in General Settings; zero retains all reports and published generations. Preserve the current publication and reports required by pending or running requests, and remove abandoned unfinished scans.",
            "FontQuarantineTask_Name" => isZh ? "隔离重复字体" : "Quarantine duplicate fonts",
            "FontQuarantineTask_Description" => isZh
                ? "隔离最新有效分析中已验证可安全替换的重复字体"
                : "Quarantine verified, safely replaceable duplicate fonts from the latest valid analysis.",
            "FontPurgeTask_Name" => isZh ? "清理过期字体隔离项" : "Purge expired font quarantine",
            "FontPurgeTask_Description" => isZh
                ? "检查已隔离满 30 天的字体，并在重新验证替代项后永久清理"
                : "Permanently purge fonts quarantined for at least 30 days after revalidating replacements.",
            "FontNormalizationTask_Name" => isZh ? "字体文件名重命名" : "Rename font files",
            "FontNormalizationTask_Description" => isZh
                ? "按当前命名规则重命名符合条件的字体文件，优先执行排队中的重命名或回滚请求"
                : "Rename eligible font files using the current naming rules, prioritizing queued rename or rollback requests.",
            _ => key
        };
    }
}
