using System;
using System.Linq;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AssSubsetter.Tests;

[Collection("PluginInstance")]
public class PluginTests
{
    private readonly Plugin _plugin;

    public PluginTests()
    {
        var mockPaths = new Mock<IApplicationPaths>();

        mockPaths.Setup(p => p.DataPath).Returns("tempData");
        mockPaths.Setup(p => p.PluginsPath).Returns("tempPlugins");

        var mockXmlSerializer = new Mock<IXmlSerializer>();
        var mockConfigManager = new Mock<MediaBrowser.Controller.Configuration.IServerConfigurationManager>();
        mockConfigManager.Setup(c => c.Configuration).Returns(new MediaBrowser.Model.Configuration.ServerConfiguration());

        _plugin = new Plugin(mockPaths.Object, mockXmlSerializer.Object, mockConfigManager.Object);
    }

    [Fact]
    public void Plugin_ShouldHaveCorrectNameAndId()
    {
        Assert.Equal("ASS Subsetter", _plugin.Name);
        Assert.Equal(Guid.Parse("7d13aa46-8b4a-ce85-9648-2cf4f52b8222"), _plugin.Id);
    }

    [Fact]
    public void GetPages_ShouldReturnConfigurationPage()
    {
        var page = Assert.Single(_plugin.GetPages(), candidate => candidate.EnableInMainMenu);

        Assert.Equal("ASS Subsetter", page.Name);
        Assert.Equal("Jellyfin.Plugin.AssSubsetter.Configuration.configPage.html", page.EmbeddedResourcePath);
    }

    [Fact]
    public void GetPages_ShouldRegisterEmbeddedAdministratorManagementPage()
    {
        var page = Assert.Single(_plugin.GetPages(), candidate => candidate.EnableInMainMenu);

        Assert.Equal("ASS Subsetter", page.Name);
        Assert.Equal("Jellyfin.Plugin.AssSubsetter.Configuration.configPage.html", page.EmbeddedResourcePath);
    }

    [Fact]
    public void GetPages_ShouldShowOnlyTheCombinedPageInDashboardSidebar()
    {
        var page = Assert.Single(_plugin.GetPages(), candidate => candidate.EnableInMainMenu);

        Assert.True(page.EnableInMainMenu);
        Assert.Equal("server", page.MenuSection);
        Assert.Equal("ASS Subsetter", page.DisplayName);
    }

    [Fact]
    public void GetPages_ShouldExposeClientScriptWithoutSidebarEntry()
    {
        var pages = _plugin.GetPages().ToArray();

        Assert.Equal(2, pages.Length);
        var script = Assert.Single(pages, page => page.Name == "ass-subsetter-config.js");
        Assert.False(script.EnableInMainMenu);
        Assert.Equal("Jellyfin.Plugin.AssSubsetter.Configuration.configPage.js", script.EmbeddedResourcePath);
    }
}
