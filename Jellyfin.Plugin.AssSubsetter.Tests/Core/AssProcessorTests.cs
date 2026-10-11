using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.Core;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;
using Jellyfin.Plugin.AssSubsetter.Models;
using Jellyfin.Plugin.AssSubsetter.Services;
using Jellyfin.Plugin.AssSubsetter.Tests.Services;
using Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Persistence;
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

        _config = new PluginConfiguration();

        var fontCacheManager = FontCacheManagerFixture.CreateManager();
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
        string inputAss = Path.Join(_tempDataPath, "missing-font.ass");
        const string content = "[V4+ Styles]\nFormat: Name, Fontname, Fontsize\nStyle: Default,MissingFont,20\n[Events]\nFormat: Layer, Start, End, Style, Text\nDialogue: 0,0:00:00.00,0:00:01.00,Default,Hello";
        await File.WriteAllTextAsync(inputAss, content, TestContext.Current.CancellationToken);

        SubsetArtifact? result = await _assProcessor.GenerateSubsetArtifactAsync(inputAss, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Empty(result.Fonts);
        Assert.Contains("Style: Default,MissingFont,20", result.AssContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateSubsetArtifactAsync_ShouldPassSubtitleCharactersToFontLookup()
    {
        string inputAss = Path.Join(_tempDataPath, "coverage.ass");
        const string content = "[V4+ Styles]\nFormat: Name, Fontname, Fontsize\nStyle: Default,ExampleFont,20\n[Events]\nFormat: Layer, Start, End, Style, Text\nDialogue: 0,0:00:00.00,0:00:01.00,Default,AB";
        await File.WriteAllTextAsync(inputAss, content, TestContext.Current.CancellationToken);
        string? requestedName = null;
        IReadOnlyCollection<uint>? requestedCharacters = null;
        var processor = new AssProcessor(
            () => _config,
            (descriptor, characters, _) =>
            {
                requestedName = descriptor.FontName;
                requestedCharacters = characters.ToArray();
                return Task.FromResult<FontFileLease?>(null);
            },
            () => "PREFIX",
            new AssDocumentParser(),
            NullLogger<AssProcessor>.Instance);

        SubsetArtifact? result = await processor.GenerateSubsetArtifactAsync(inputAss, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("ExampleFont", requestedName);
        Assert.NotNull(requestedCharacters);
        Assert.Equal([65u, 66u], requestedCharacters.OrderBy(character => character).ToArray());
        Assert.Empty(result.Fonts);
    }

    [Fact]
    public async Task GenerateSubsetArtifactAsync_ShouldNotAccessRepositoryWhenRuntimeSnapshotIsCold()
    {
        string inputAss = Path.Join(_tempDataPath, "cold-snapshot.ass");
        const string content = "[V4+ Styles]\nFormat: Name, Fontname, Fontsize\nStyle: Default,MissingFont,20\n[Events]\nFormat: Layer, Start, End, Style, Text\nDialogue: 0,0:00:00.00,0:00:01.00,Default,Hello";
        await File.WriteAllTextAsync(inputAss, content, TestContext.Current.CancellationToken);
        var repository = new InMemoryFontRepository();
        var scanner = new FontInventoryScanner(
            repository,
            () => new FontDirectoryPolicyResolver(null, null, null, includePlatformDefaults: false),
            new PathPhysicalFileIdentityProvider(),
            NullLogger<FontInventoryScanner>.Instance);
        using var manager = new FontCacheManager(repository, scanner, new FontMaintenanceCoordinator());
        var processor = new AssProcessor(() => _config, manager, new AssDocumentParser(), NullLogger<AssProcessor>.Instance);

        SubsetArtifact? result = await processor.GenerateSubsetArtifactAsync(inputAss, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Empty(result.Fonts);
        Assert.Equal(0, repository.InitializeCallCount);
        Assert.Equal(0, repository.RuntimeSnapshotLoadCallCount);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDataPath))
        {
            try { Directory.Delete(_tempDataPath, true); } catch (IOException) { /* Ignore */ } catch (UnauthorizedAccessException) { /* Ignore */ }
        }
    }
}
