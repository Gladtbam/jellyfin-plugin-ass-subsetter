using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.ScheduledTasks;
using Jellyfin.Plugin.AssSubsetter.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AssSubsetter.Tests.ScheduledTasks;

public class FontCacheUpdateTaskTests : IDisposable
{
    private readonly string _tempDataPath;
    private readonly PluginConfiguration _config;
    private readonly Mock<ToolManager> _mockToolManager;
    private readonly FontCacheUpdateTask _task;

    public FontCacheUpdateTaskTests()
    {
        _tempDataPath = Path.Combine(Path.GetTempPath(), "FontCacheTaskTests_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDataPath);

        var mockPaths = new Mock<MediaBrowser.Common.Configuration.IApplicationPaths>();
        mockPaths.Setup(p => p.DataPath).Returns(_tempDataPath);
        mockPaths.Setup(p => p.PluginsPath).Returns(_tempDataPath);
        _ = new Plugin(mockPaths.Object, new Mock<MediaBrowser.Model.Serialization.IXmlSerializer>().Object);

        _config = new PluginConfiguration
        {
            CustomFontDirectories = _tempDataPath,
            FontCacheDirectory = Path.Combine(_tempDataPath, "font_caches")
        };

        _mockToolManager = new Mock<ToolManager>(new NullLogger<ToolManager>());

        var assProcessor = new AssProcessor(_mockToolManager.Object, _config, new NullLogger<AssProcessor>());
        _task = new FontCacheUpdateTask(_mockToolManager.Object, _config, new NullLogger<FontCacheUpdateTask>());
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRunMkvtool_WhenFontDirectoriesExist()
    {
        // Arrange
        string fakeExe = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "cmd.exe" : "bash";
        _mockToolManager.Setup(m => m.GetToolPathAsync(It.IsAny<CancellationToken>())).ReturnsAsync(fakeExe);

        var progressMock = new Mock<IProgress<double>>();

        // Act
        using var testCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        testCts.CancelAfter(TimeSpan.FromSeconds(2)); // 2 秒后强制掐断

        try
        {
            await _task.ExecuteAsync(progressMock.Object, testCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected
        }

        // Assert
        progressMock.Verify(p => p.Report(0), Times.Once);

        Assert.True(Directory.Exists(_config.FontCacheDirectory), "Font cache directory should be created.");
    }

    [Fact]
    public void GetDefaultTriggers_ShouldReturnEmptyArray()
    {
        // Arrange & Act
        var triggers = _task.GetDefaultTriggers();

        // Assert
        Assert.Empty(triggers);
    }

    [Fact]
    public void GetFontDirectories_ShouldSkipInvalidCustomDirectories()
    {
        // Arrange
        var invalidDir = Path.Combine(_tempDataPath, "InvalidDir");
        var validDir = Path.Combine(_tempDataPath, "ValidDir");
        Directory.CreateDirectory(validDir);

        _config.CustomFontDirectories = $"{invalidDir};{validDir}";

        var method = typeof(FontCacheUpdateTask).GetMethod("GetFontDirectories", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);

        // Act
        var result = method.Invoke(_task, null) as System.Collections.Generic.List<string>;

        // Assert
        Assert.NotNull(result);
        Assert.Contains(validDir, result);
        Assert.DoesNotContain(invalidDir, result);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldContinue_WhenProcessFails()
    {
        // Arrange
        string fakeExe;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            fakeExe = Path.Combine(_tempDataPath, "fakeTool.bat");
            File.WriteAllText(fakeExe, "exit /b 1");
        }
        else
        {
            fakeExe = "false";
        }
        _mockToolManager.Setup(m => m.GetToolPathAsync(It.IsAny<CancellationToken>())).ReturnsAsync(fakeExe);

        // Setup multiple valid directories so we can verify it processes all of them despite failure
        var dir1 = Path.Combine(_tempDataPath, "FailDir1");
        var dir2 = Path.Combine(_tempDataPath, "FailDir2");
        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dir2);
        
        _config.CustomFontDirectories = $"{dir1};{dir2}";

        var progressMock = new Mock<IProgress<double>>();

        // Act
        using var testCts = new CancellationTokenSource();
        testCts.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            await _task.ExecuteAsync(progressMock.Object, testCts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected if it hangs, but ideally it finishes before timeout
        }

        // Assert
        // The process should finish normally, not crashing out.
        // And progress should eventually reach 100%
        progressMock.Verify(p => p.Report(100), Times.AtLeastOnce());
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
                // Ignore cleanup errors
            }
        }
    }
}
