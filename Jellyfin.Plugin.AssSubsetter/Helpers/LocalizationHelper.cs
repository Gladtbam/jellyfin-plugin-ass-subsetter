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
            "FontMaintenanceTask_Name" => isZh ? "ASS Subsetter 字体维护" : "ASS Subsetter font maintenance",
            "FontMaintenanceTask_Description" => isZh
                ? "按插件设置执行字体库存分析及历史清理、重复字体隔离及过期清理、字体文件重命名。"
                : "Runs the enabled stages from plugin settings: inventory analysis and history pruning, duplicate quarantine and expiry cleanup, and filename renaming.",
            _ => key
        };
    }
}
