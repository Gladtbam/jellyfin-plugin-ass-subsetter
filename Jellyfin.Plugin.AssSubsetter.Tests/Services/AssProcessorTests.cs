using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Services;

public class AssProcessorTests : IDisposable
{
    private readonly string _tempDataPath;
    private readonly Mock<ToolManager> _mockToolManager;
    private readonly AssProcessor _assProcessor;

    public AssProcessorTests()
    {
        _tempDataPath = Path.Combine(Path.GetTempPath(), "AssProcessorTests_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDataPath);

        var mockPaths = new Mock<IApplicationPaths>();
        mockPaths.Setup(p => p.DataPath).Returns(_tempDataPath);
        mockPaths.Setup(p => p.PluginsPath).Returns(_tempDataPath);

        var mockXmlSerializer = new Mock<IXmlSerializer>();

        _ = new Plugin(mockPaths.Object, mockXmlSerializer.Object);

        var configInstance = new PluginConfiguration
        {
            FontCacheDirectory = _tempDataPath
        };

        _mockToolManager = new Mock<ToolManager>(new NullLogger<ToolManager>());

        _assProcessor = new AssProcessor(_mockToolManager.Object, configInstance, new NullLogger<AssProcessor>());
    }

    [Fact]
    public async Task GenerateSubsetFontAsync_ShouldReturnFalse_WhenToolPathIsInvalid()
    {
        _mockToolManager.Setup(m => m.GetToolPathAsync(It.IsAny<CancellationToken>())).ReturnsAsync("Z:\\invalid_non_existent_executable.exe");

        bool result = await _assProcessor.GenerateSubsetFontAsync("input.ass", "output.ass", TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task CreateFakeTokenTest_WhenCancellationTokenIsCancelled()
    {
        string fakeExe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "bash";
        _mockToolManager.Setup(m => m.GetToolPathAsync(It.IsAny<CancellationToken>())).ReturnsAsync(fakeExe);

        using var testCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        testCts.Cancel();

        bool result = await _assProcessor.GenerateSubsetFontAsync("input.ass", "output.ass", testCts.Token);

        Assert.False(result);
    }

    [Fact]
    public async Task GenerateSubsetFontAsync_ShouldReturnFalse_WhenProcessFails()
    {
        string inputAss = Path.Combine(_tempDataPath, "dummy_input.ass");
        string outputAss = Path.Combine(_tempDataPath, "dummy_output.ass");

        string fakeExe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "bash";
        _mockToolManager.Setup(m => m.GetToolPathAsync(It.IsAny<CancellationToken>())).ReturnsAsync(fakeExe);

        // Act
        bool result = await _assProcessor.GenerateSubsetFontAsync(inputAss, outputAss, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result, "Process should return false when failing.");
        Assert.False(File.Exists(outputAss), "Failed process should not create target output file.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDataPath))
        {
            try
            {
                Directory.Delete(_tempDataPath, true);
            }
            catch
            {
                 /* Ignore */
            }
        }
    }
}
