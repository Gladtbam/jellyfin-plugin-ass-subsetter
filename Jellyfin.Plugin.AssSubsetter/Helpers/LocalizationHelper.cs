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
            _ => key
        };
    }
}
