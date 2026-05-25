using System;
using System.IO;
using Jellyfin.Plugin.AssSubsetter.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Services;

public class ToolManagerTests : IDisposable
{
    private readonly string _tempDataPath;
    private readonly ToolManager _toolManager;

    public ToolManagerTests()
    {
        _tempDataPath = Path.Combine(Path.GetTempPath(), "JellyfinAssSubsetterTests_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDataPath);

        var mockPaths = new Mock<IApplicationPaths>();
        mockPaths.Setup(p => p.DataPath).Returns(_tempDataPath);
        mockPaths.Setup(p => p.PluginsPath).Returns(_tempDataPath);

        var mockXmlSerializer = new Mock<IXmlSerializer>();

        var plugin = new Plugin(mockPaths.Object, mockXmlSerializer.Object);

        _toolManager = new ToolManager(new NullLogger<ToolManager>());
    }

    [Fact]
    public void GetToolPath_ShouldExtractBinary_WhenNotExists()
    {
        try
        {
            string toolPath = _toolManager.GetToolPath();

            Assert.False(string.IsNullOrEmpty(toolPath));
            Assert.True(File.Exists(toolPath), $"Expected extracted file at {toolPath}");
        }
        catch (FileNotFoundException ex)
        {
            Assert.Contains("Cannot find embedded resource", ex.Message);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDataPath))
        {
            Directory.Delete(_tempDataPath, true);
        }
    }
}
