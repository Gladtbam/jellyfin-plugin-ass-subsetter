using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AssSubsetter.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class.
    /// </summary>
    public PluginConfiguration()
    {
        EnableAutoScanProcessing = true;
        EnablePrefetchSubsetting = true;
        PrefetchTriggerPercent = 90;
        MaxCacheSizeMB = 1024;
        FontCacheDirectory = string.Empty;
        CustomFontDirectories = string.Empty;
        FallbackToOriginalOnError = true;
    }

    /// <summary>
    /// Gets or sets a value indicating whether auto scan processing is enabled.
    /// </summary>
    public bool EnableAutoScanProcessing { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether prefetch subsetting for the next episode is enabled.
    /// When enabled, the plugin will automatically subset the next episode's subtitles
    /// when playback progress reaches the configured threshold.
    /// </summary>
    public bool EnablePrefetchSubsetting { get; set; }

    /// <summary>
    /// Gets or sets the playback progress percentage (0-100) at which to trigger
    /// prefetch subsetting for the next episode's subtitles.
    /// </summary>
    public int PrefetchTriggerPercent { get; set; }

    /// <summary>
    /// Gets or sets the maximum cache size in megabytes.
    /// </summary>
    public int MaxCacheSizeMB { get; set; }

    /// <summary>
    /// Gets or sets the font cache directory.
    /// </summary>
    public string FontCacheDirectory { get; set; }

    /// <summary>
    /// Gets or sets custom font directories, separated by semicolons.
    /// </summary>
    public string CustomFontDirectories { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets a value indicating whether to fallback to the original subtitle on error.
    /// </summary>
    public bool FallbackToOriginalOnError { get; set; }
}
