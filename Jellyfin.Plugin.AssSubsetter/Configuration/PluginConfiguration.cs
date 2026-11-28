using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
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
    ConvertToSup = 1,

    /// <summary>Package rewritten ASS and subset fonts into MKS via Jellyfin FFmpeg.</summary>
    GenerateMks = 2
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
        FontHistoryRetentionCount = 3;
        FontInventoryScanConcurrency = 4;
        FontDeepAnalysisConcurrency = 2;
        EnableScheduledFontAnalysis = true;
        CustomFontDirectories = string.Empty;
        ProtectedFontDirectories = string.Empty;
        PreferredFontDirectories = string.Empty;
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
    ///     Gets or sets how many recent analysis reports and published inventory generations to retain. Zero retains all.
    /// </summary>
    public int FontHistoryRetentionCount { get; set; }

    /// <summary>
    ///     Gets or sets the concurrency limit for font inventory scanning and validation before publication.
    ///     The effective limit is between one and the number of logical processors available to the Jellyfin process.
    /// </summary>
    public int FontInventoryScanConcurrency { get; set; }

    /// <summary>
    ///     Gets or sets the maximum concurrent inspections of distinct font contents during deep analysis.
    ///     The effective limit is between one and the number of logical processors available to the Jellyfin process.
    /// </summary>
    public int FontDeepAnalysisConcurrency { get; set; }

    /// <summary>Gets or sets a value indicating whether scheduled maintenance updates the inventory, analyzes duplicates, and prunes history.</summary>
    public bool EnableScheduledFontAnalysis { get; set; }

    /// <summary>Gets or sets a value indicating whether scheduled maintenance quarantines verified duplicates and purges expired quarantine items.</summary>
    public bool EnableScheduledFontQuarantine { get; set; }

    /// <summary>Gets or sets a value indicating whether the scheduled font maintenance task renames fonts.</summary>
    public bool EnableScheduledFontRenaming { get; set; }

    /// <summary>Gets or sets the filename stem template; source extensions are appended in lowercase.</summary>
    public string FontFileNameTemplate { get; set; } = FontNamingRules.DefaultFileNameTemplate;

    /// <summary>Gets or sets the source-root-relative folder template; empty preserves the parent.</summary>
    public string FontFolderTemplate { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether empty source ancestors are removed.</summary>
    public bool RemoveEmptyFontDirectories { get; set; }

    /// <summary>
    ///     Gets or sets custom font directories, separated by semicolons.
    /// </summary>
    public string CustomFontDirectories { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets additional protected font directories, separated by semicolons.
    /// </summary>
    public string ProtectedFontDirectories { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets preferred font directories, separated by semicolons.
    /// </summary>
    public string PreferredFontDirectories { get; set; } = string.Empty;

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
