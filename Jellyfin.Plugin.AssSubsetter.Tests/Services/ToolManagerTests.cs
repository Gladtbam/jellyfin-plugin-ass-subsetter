using System;
using System.IO;
using Jellyfin.Plugin.AssSubsetter.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

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

        _ = new Plugin(mockPaths.Object, mockXmlSerializer.Object);

        _toolManager = new ToolManager(new NullLogger<ToolManager>());
    }

    [Fact]
    public async Task GetToolPathAsync_ShouldReturnCorrectPath_WhenCalled()
    {
        try
        {
            string toolPath = await _toolManager.GetToolPathAsync(TestContext.Current.CancellationToken);

            Assert.False(string.IsNullOrEmpty(toolPath));
            Assert.True(File.Exists(toolPath), $"Expected extracted file at {toolPath}");
        }
        catch (FileNotFoundException ex)
        {
            Assert.Contains("Cannot find embedded resource", ex.Message);
        }
    }

    [Fact]
    public async Task GetToolPathAsync_ShouldCleanupOldVersions_WhenCalled()
    {
        string pluginDataPath = Plugin.Instance!.PluginDataPath;
        Directory.CreateDirectory(pluginDataPath);

        string oldVersionPath = Path.Combine(pluginDataPath, "mkvtool-v0.9.0-windows-amd64.exe");
        string tmpFilePath = Path.Combine(pluginDataPath, "mkvtool-v1.0.0-linux-amd64.tmp");
        string innocentFilePath = Path.Combine(pluginDataPath, "other-plugin-data.json");

        await File.WriteAllTextAsync(oldVersionPath, "dummy old data");
        await File.WriteAllTextAsync(tmpFilePath, "dummy tmp data");
        await File.WriteAllTextAsync(innocentFilePath, "innocent data");

        Assert.True(File.Exists(oldVersionPath));
        Assert.True(File.Exists(tmpFilePath));

        string currentToolPath = await _toolManager.GetToolPathAsync(TestContext.Current.CancellationToken);

        Assert.True(File.Exists(currentToolPath), "The current version should be successfully downloaded.");

        Assert.False(File.Exists(oldVersionPath), "The old version file should have been CLEANED UP.");
        Assert.False(File.Exists(tmpFilePath), "The temporary .tmp file should have been CLEANED UP.");

        Assert.True(File.Exists(innocentFilePath), "Innocent files not starting with 'mkvtool' should be spared.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDataPath))
        {
            Directory.Delete(_tempDataPath, true);
        }
    }
}
