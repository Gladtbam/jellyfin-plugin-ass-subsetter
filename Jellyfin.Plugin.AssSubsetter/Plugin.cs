using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Helpers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.AssSubsetter;

/// <summary>
///     The main plugin.
/// </summary>
public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="Plugin" /> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths" /> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer" /> interface.</param>
    /// <param name="configManager">Instance of the <see cref="IServerConfigurationManager" /> interface.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, IServerConfigurationManager configManager)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        StoragePaths = FontStoragePaths.Create(applicationPaths.DataPath);
        PluginDataPath = StoragePaths.RootPath;
        PluginCachePath = Path.Join(applicationPaths.CachePath, "ass-subsetter");

        // Initialize localization based on server UI culture
        LocalizationHelper.Initialize(configManager.Configuration.UICulture);
        configManager.ConfigurationUpdated += (_, _) =>
        {
            LocalizationHelper.Initialize(configManager.Configuration.UICulture);
        };
    }

    /// <summary>Gets the plugin name displayed in the Jellyfin dashboard.</summary>
    public override string Name => "ASS Subsetter";

    /// <summary>Gets the stable plugin identifier used for configuration and scheduled-task ownership.</summary>
    public override Guid Id => Guid.Parse("7d13aa46-8b4a-ce85-9648-2cf4f52b8222");

    /// <summary>
    ///     Gets the Jellyfin program data path for storing persistent plugin files.
    /// </summary>
    public string PluginDataPath { get; }

    /// <summary>
    ///     Gets the fixed persistent storage paths for the font catalog and maintenance operations.
    /// </summary>
    public FontStoragePaths StoragePaths { get; }

    /// <summary>
    ///     Gets the plugin-specific cache folder under Jellyfin's cache directory.
    /// </summary>
    public string PluginCachePath { get; }

    /// <summary>
    ///     Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>Gets the combined settings and font management page.</summary>
    /// <returns>The plugin's dashboard page registration.</returns>
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                DisplayName = "ASS Subsetter",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace),
                EnableInMainMenu = true,
                MenuSection = "server"
            },
            new PluginPageInfo
            {
                Name = "ass-subsetter-config.js",
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.js", GetType().Namespace)
            }
        ];
    }
}
