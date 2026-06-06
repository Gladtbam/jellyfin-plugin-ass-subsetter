using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Services;

public class AssProcessorTests : IDisposable
{
    private readonly string _tempDataPath;
    private readonly PluginConfiguration _config;
    private readonly AssProcessor _assProcessor;

    public AssProcessorTests()
    {
        _tempDataPath = Path.Combine(Path.GetTempPath(), "AssProcessorTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDataPath);

        _config = new PluginConfiguration
        {
            FontCacheFilePath = Path.Combine(_tempDataPath, "font_caches.json")
        };

        var fontCacheManager = new FontCacheManager(NullLogger<FontCacheManager>.Instance, () => _config);
        var assParser = new AssDocumentParser();
        _assProcessor = new AssProcessor(_config, fontCacheManager, assParser, NullLogger<AssProcessor>.Instance);
    }

    [Fact]
    public async Task GenerateSubsetFontAsync_ShouldCopyFileAsIs_WhenNoFontsUsed()
    {
        // Arrange
        string inputAss = Path.Combine(_tempDataPath, "input.ass");
        string outputAss = Path.Combine(_tempDataPath, "output.ass");

        // Simple text, no font overrides, no V4 Styles
        File.WriteAllText(inputAss, "Dialogue: 0,0:00:00.00,0:00:05.00,Default,,0,0,0,,Plain Text");

        // Act
        bool result = await _assProcessor.GenerateSubsetFontAsync(inputAss, outputAss, CancellationToken.None);

        // Assert
        Assert.True(result);
        Assert.True(File.Exists(outputAss));
        
        string content = File.ReadAllText(outputAss);
        Assert.Equal("Dialogue: 0,0:00:00.00,0:00:05.00,Default,,0,0,0,,Plain Text", content);
    }

    [Fact]
    public async Task GenerateSubsetFontAsync_ShouldCatchCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Pass invalid paths; it should return false due to cancellation before doing anything major,
        // or during the first async wait if any.
        bool result = await _assProcessor.GenerateSubsetFontAsync("dummy_input.ass", "dummy_output.ass", cts.Token);

        Assert.False(result);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDataPath))
        {
            try { Directory.Delete(_tempDataPath, true); } catch { /* Ignore */ }
        }
    }
}
