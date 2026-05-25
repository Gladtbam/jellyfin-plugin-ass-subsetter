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
