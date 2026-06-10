using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Jellyfin.Plugin.AssSubsetter.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.AssSubsetter;

/// <summary>
/// The main plugin.
/// </summary>
public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        PluginDataPath = applicationPaths.DataPath;
        PluginCachePath = Path.Join(applicationPaths.CachePath, "ass-subsetter");
    }

    /// <inheritdoc />
    public override string Name => "ASS Subsetter";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("7d13aa46-8b4a-ce85-9648-2cf4f52b8222");

    /// <summary>
    /// Gets the Jellyfin program data path for storing persistent plugin files.
    /// </summary>
    public string PluginDataPath { get; }

    /// <summary>
    /// Gets the plugin-specific cache folder under Jellyfin's cache directory.
    /// </summary>
    public string PluginCachePath { get; }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            }
        ];
    }
}
