using System;
using System.Linq;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AssSubsetter.Tests;

public class PluginTests
{
    private readonly Plugin _plugin;

    public PluginTests()
    {
        var mockPaths = new Mock<IApplicationPaths>();

        mockPaths.Setup(p => p.DataPath).Returns("tempData");
        mockPaths.Setup(p => p.PluginsPath).Returns("tempPlugins");

        var mockXmlSerializer = new Mock<IXmlSerializer>();

        _plugin = new Plugin(mockPaths.Object, mockXmlSerializer.Object);
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
        var pages = _plugin.GetPages().ToList();

        Assert.Single(pages);

        Assert.Equal("ASS Subsetter", pages[0].Name);
        Assert.Contains("configPage.html", pages[0].EmbeddedResourcePath);
    }
}
