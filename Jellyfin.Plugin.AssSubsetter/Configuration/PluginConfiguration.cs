using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AssSubsetter.Configuration;

/// <summary>
///     Subtitle processing mode.
/// </summary>
public enum SubtitleProcessingMode
{
    /// <summary>Font subsetting mode (default). Subsets fonts and embeds them in ASS.</summary>
    Subsetting = 0,

    /// <summary>Convert ASS to SUP (PGS) bitmap subtitle mode using libass rendering.</summary>
    ConvertToSup = 1
}

/// <summary>
///     Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="PluginConfiguration" /> class.
    /// </summary>
    public PluginConfiguration()
    {
        SubtitleMode = SubtitleProcessingMode.Subsetting;
        EnableAutoScanProcessing = true;
        EnablePrefetchSubsetting = true;
        PrefetchTriggerPercent = 85;
        MaxCacheSizeMB = 1024;
        FontCacheFilePath = string.Empty;
        CustomFontDirectories = string.Empty;
        FallbackToOriginalOnError = true;
        AssToSupFrameRate = 24;
    }

    /// <summary>
    ///     Gets or sets the subtitle processing mode.
    ///     Subsetting and ConvertToSup are mutually exclusive.
    /// </summary>
    public SubtitleProcessingMode SubtitleMode { get; set; }

    /// <summary>
    ///     Gets or sets a value indicating whether auto scan processing is enabled.
    /// </summary>
    public bool EnableAutoScanProcessing { get; set; }

    /// <summary>
    ///     Gets or sets a value indicating whether prefetch subsetting for the next episode is enabled.
    ///     When enabled, the plugin will automatically subset the next episode's subtitles
    ///     when playback progress reaches the configured threshold.
    /// </summary>
    public bool EnablePrefetchSubsetting { get; set; }

    /// <summary>
    ///     Gets or sets the playback progress percentage (0-100) at which to trigger
    ///     prefetch subsetting for the next episode's subtitles.
    /// </summary>
    public int PrefetchTriggerPercent { get; set; }

    /// <summary>
    ///     Gets or sets the maximum cache size in megabytes.
    /// </summary>
    public int MaxCacheSizeMB { get; set; }

    /// <summary>
    ///     Gets or sets the font cache JSON file path.
    /// </summary>
    public string FontCacheFilePath { get; set; }

    /// <summary>
    ///     Gets or sets custom font directories, separated by semicolons.
    /// </summary>
    public string CustomFontDirectories { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets a value indicating whether to fallback to the original subtitle on error.
    /// </summary>
    public bool FallbackToOriginalOnError { get; set; }

    /// <summary>
    ///     Gets or sets the frame rate used for ASS to SUP conversion scanning.
    ///     Higher values produce smoother animations but larger files. Default is 24.
    /// </summary>
    public int AssToSupFrameRate { get; set; }
}
