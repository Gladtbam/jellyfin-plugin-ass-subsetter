using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.Core;
using Jellyfin.Plugin.AssSubsetter.Models;
using Jellyfin.Plugin.AssSubsetter.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Core;

public class AssProcessorTests : IDisposable
{
    private readonly string _tempDataPath;
    private readonly PluginConfiguration _config;
    private readonly AssProcessor _assProcessor;

    public AssProcessorTests()
    {
        _tempDataPath = Path.Join(Path.GetTempPath(), "AssProcessorTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDataPath);

        _config = new PluginConfiguration
        {
            FontCacheFilePath = Path.Join(_tempDataPath, "font_caches.json")
        };

        var fontCacheManager = new FontCacheManager(NullLogger<FontCacheManager>.Instance, () => _config);
        var assParser = new AssDocumentParser();
        _assProcessor = new AssProcessor(() => _config, fontCacheManager, assParser, NullLogger<AssProcessor>.Instance);
    }

    [Fact]
    public async Task GenerateSubsetFontAsync_ShouldCopyFileAsIs_WhenNoFontsUsed()
    {
        // Arrange
        string inputAss = Path.Join(_tempDataPath, "input.ass");
        string outputAss = Path.Join(_tempDataPath, "output.ass");
        const string content = "Dialogue: 0,0:00:00.00,0:00:05.00,Default,,0,0,0,,Plain Text";

        // Simple text, no font overrides, no V4 Styles
        await File.WriteAllTextAsync(inputAss, content, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(outputAss, "old-cache", TestContext.Current.CancellationToken);

        // Act
        bool result = await _assProcessor.GenerateSubsetFontAsync(inputAss, outputAss, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(result);
        Assert.True(File.Exists(outputAss));
        Assert.Equal(content, await File.ReadAllTextAsync(outputAss, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_tempDataPath, "*.partial.ass"));
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

    [Fact]
    public async Task GenerateSubsetArtifactAsync_ShouldReturnPlainAss_WhenNoFontsUsed()
    {
        string inputAss = Path.Join(_tempDataPath, "plain.ass");
        const string content = "Dialogue: 0,0:00:00.00,0:00:05.00,Default,,0,0,0,,Plain Text";
        await File.WriteAllTextAsync(inputAss, content, TestContext.Current.CancellationToken);

        SubsetArtifact? result = await _assProcessor.GenerateSubsetArtifactAsync(inputAss, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(content, result.AssContent);
        Assert.Empty(result.Fonts);
        Assert.DoesNotContain("[Fonts]", result.AssContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateSubsetArtifactAsync_ShouldReturnNull_WhenCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        SubsetArtifact? result = await _assProcessor.GenerateSubsetArtifactAsync("dummy_input.ass", cts.Token);

        Assert.Null(result);
    }

    [Fact]
    public async Task GenerateSubsetArtifactAsync_ShouldKeepOriginalName_WhenFontCannotBeFound()
    {
        await File.WriteAllTextAsync(_config.FontCacheFilePath, "[]", TestContext.Current.CancellationToken);
        string inputAss = Path.Join(_tempDataPath, "missing-font.ass");
        const string content = "[V4+ Styles]\nFormat: Name, Fontname, Fontsize\nStyle: Default,MissingFont,20\n[Events]\nFormat: Layer, Start, End, Style, Text\nDialogue: 0,0:00:00.00,0:00:01.00,Default,Hello";
        await File.WriteAllTextAsync(inputAss, content, TestContext.Current.CancellationToken);

        SubsetArtifact? result = await _assProcessor.GenerateSubsetArtifactAsync(inputAss, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Empty(result.Fonts);
        Assert.Contains("Style: Default,MissingFont,20", result.AssContent, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDataPath))
        {
            try { Directory.Delete(_tempDataPath, true); } catch (IOException) { /* Ignore */ } catch (UnauthorizedAccessException) { /* Ignore */ }
        }
    }
}
