using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.Core;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;
using Jellyfin.Plugin.AssSubsetter.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Services;

public sealed class SelectiveSubtitleCacheTests
{
    [Theory]
    [InlineData(SubtitleProcessingMode.Subsetting, false)]
    [InlineData(SubtitleProcessingMode.Subsetting, true)]
    [InlineData(SubtitleProcessingMode.GenerateMks, false)]
    [InlineData(SubtitleProcessingMode.GenerateMks, true)]
    [InlineData(SubtitleProcessingMode.ConvertToSup, false)]
    [InlineData(SubtitleProcessingMode.ConvertToSup, true)]
    public async Task UnreadableDependenciesRespectFallbackPolicy(SubtitleProcessingMode mode, bool fallback)
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "locked.ass");
            await File.WriteAllTextAsync(source, "locked", TestContext.Current.CancellationToken);
            using var exclusive = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            using var manager = FontCacheManagerFixture.CreateManager();
            var config = new PluginConfiguration { SubtitleMode = mode, FallbackToOriginalOnError = fallback };
            var processor = new AssProcessor(() => config, manager, new AssDocumentParser(), NullLogger<AssProcessor>.Instance);
            using var service = new SubtitleCacheService(() => config, processor, null, null, Path.Combine(root, "cache"), NullLogger<SubtitleCacheService>.Instance, manager);
            var result = await service.GetOrGenerateSubtitleAsync(Guid.NewGuid(), source, cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(result.IsReady);
            Assert.Equal(fallback ? source : string.Empty, result.Path);
            Assert.Empty(Directory.GetFiles(service.CacheFolderPath));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(SubtitleProcessingMode.Subsetting)]
    [InlineData(SubtitleProcessingMode.GenerateMks)]
    [InlineData(SubtitleProcessingMode.ConvertToSup)]
    public async Task PublishedChangesInvalidateOnlyDependentCacheEntries(SubtitleProcessingMode mode)
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "source.ass");
            await File.WriteAllTextAsync(source, "[V4+ Styles]\nFormat: Name, Fontname, Fontsize, Bold, Italic\nStyle: Default,Example,20,0,0\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,A", TestContext.Current.CancellationToken);
            IReadOnlyList<RuntimeFontFace> faces = [];
            var repository = new Mock<IFontRepository>();
            repository.Setup(r => r.LoadPublishedRuntimeFacesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => faces);
            using var manager = FontCacheManagerFixture.CreateManager(repository.Object);
            var config = new PluginConfiguration { SubtitleMode = mode };
            var processor = new AssProcessor(() => config, manager, new AssDocumentParser(), NullLogger<AssProcessor>.Instance);
            using var service = new SubtitleCacheService(() => config, processor, null, null, Path.Combine(root, "cache"), NullLogger<SubtitleCacheService>.Instance, manager);
            Guid item = Guid.NewGuid();
            async Task<string> Seed()
            {
                await manager.LoadPublishedSnapshotAsync(null, TestContext.Current.CancellationToken);
                string fingerprint = Jellyfin.Plugin.AssSubsetter.Helpers.SubtitleCacheFingerprint.Create(source, mode,
                    1920, 1080, Math.Clamp(config.AssToSupFrameRate, 10, 60), manager.GetSubtitleDependencies(source).Fingerprint);
                string extension = mode == SubtitleProcessingMode.Subsetting ? "ass" : mode == SubtitleProcessingMode.GenerateMks ? "mks" : "sup";
                string path = Path.Combine(service.CacheFolderPath, $"{item:N}_{fingerprint}.{extension}");
                await File.WriteAllTextAsync(path, "cached", TestContext.Current.CancellationToken);
                return path;
            }

            async Task AssertHit(string expected)
            {
                var result = await service.GetOrGenerateSubtitleAsync(item, source, cancellationToken: TestContext.Current.CancellationToken);
                Assert.True(result.IsReady);
                Assert.Equal(expected, result.Path);
            }

            string missing = await Seed();
            await AssertHit(missing);
            var selected = new RuntimeFontFace(1, "old.ttf", 0, "Example", "Regular", ["Example"], 400, 5, 0, false, false, "TrueType", 0, true)
            { ContentHash = "ABCD", UnicodeCoverage = [new Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.UnicodeRange(65, 65)] };
            var unrelated = selected with { FaceId = 2, FamilyName = "Unrelated", Names = ["Unrelated"], ContentHash = "FFFF" };
            faces = [unrelated];
            await manager.LoadPublishedSnapshotAsync(null, TestContext.Current.CancellationToken);
            await AssertHit(missing);
            faces = [selected, unrelated];
            string present = await Seed();
            Assert.NotEqual(missing, present);
            await AssertHit(present);
            faces = [selected with { Path = "moved.ttf", FaceId = 3 }, unrelated with { ContentHash = "EEEE" }];
            await manager.LoadPublishedSnapshotAsync(null, TestContext.Current.CancellationToken);
            await AssertHit(present);
            faces = [selected with { ContentHash = "1234" }];
            string changed = await Seed();
            Assert.NotEqual(present, changed);
            await AssertHit(changed);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(SubtitleProcessingMode.Subsetting)]
    [InlineData(SubtitleProcessingMode.GenerateMks)]
    public async Task CacheRebuildsOnlyWhenSelectedFontChanges(SubtitleProcessingMode mode)
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "source.ass");
            const string document = "[V4+ Styles]\nFormat: Name, Fontname, Fontsize, Bold, Italic\nStyle: Default,Example,20,0,0\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,A";
            await File.WriteAllTextAsync(source, document, TestContext.Current.CancellationToken);
            string fontPath = Path.Combine(root, "font.ttf");
            byte[] bytes = [1, 2, 3];
            await File.WriteAllBytesAsync(fontPath, bytes, TestContext.Current.CancellationToken);
            IReadOnlyList<RuntimeFontFace> faces = [];
            var repository = new Mock<IFontRepository>();
            repository.Setup(r => r.LoadPublishedRuntimeFacesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => faces);
            using var manager = FontCacheManagerFixture.CreateManager(repository.Object);
            var config = new PluginConfiguration { SubtitleMode = mode };
            var processor = new Mock<AssProcessor>((Func<PluginConfiguration>)(() => config), manager, new AssDocumentParser(), NullLogger<AssProcessor>.Instance) { CallBase = true };
            int generated = 0;
            int muxed = 0;
            processor.Setup(p => p.GenerateSubsetArtifactAsync(source, It.IsAny<CancellationToken>()))
                .Returns(async (string path, CancellationToken token) =>
                {
                    generated++;
                    foreach (var request in new AssDocumentParser().ExtractUsedCharacters(path))
                    {
                        await using var lease = await manager.AcquireFontAsync(request.Key, request.Value, token);
                        byte[]? consumed = lease is null ? null : await File.ReadAllBytesAsync(lease.Path, token);
                        manager.ValidateFontDependency(request.Key, request.Value, lease, consumed);
                    }

                    return new Jellyfin.Plugin.AssSubsetter.Models.SubsetArtifact(document, []);
                });
            var muxer = new MksMuxer(NullLogger<MksMuxer>.Instance, () => "ffmpeg", async (info, token) =>
            {
                muxed++;
                await File.WriteAllTextAsync(info.ArgumentList[^1], "mks", token);
                return 0;
            });
            using var service = new SubtitleCacheService(() => config, processor.Object, null, muxer, Path.Combine(root, "cache"), NullLogger<SubtitleCacheService>.Instance, manager);
            Guid item = Guid.NewGuid();
            async Task<string> Request(int expectedGenerations)
            {
                await manager.LoadPublishedSnapshotAsync(null, TestContext.Current.CancellationToken);
                var result = await service.GetOrGenerateSubtitleAsync(item, source, cancellationToken: TestContext.Current.CancellationToken);
                Assert.True(result.IsReady);
                Assert.True(File.Exists(result.Path));
                Assert.Equal(expectedGenerations, generated);
                Assert.Equal(mode == SubtitleProcessingMode.GenerateMks ? expectedGenerations : 0, muxed);
                return result.Path;
            }

            string missing = await Request(1);
            Assert.Equal(missing, await Request(1));
            var selected = new RuntimeFontFace(1, fontPath, 0, "Example", "Regular", ["Example"], 400, 5, 0, false, false, "TrueType", 0, true)
            {
                ContentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)),
                UnicodeCoverage = [new Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.UnicodeRange(65, 65)]
            };
            var unrelated = selected with { FaceId = 2, FamilyName = "Unrelated", Names = ["Unrelated"] };
            faces = [unrelated];
            Assert.Equal(missing, await Request(1));
            faces = [selected, unrelated];
            string resolved = await Request(2);
            Assert.NotEqual(missing, resolved);
            faces = [selected, unrelated with { ContentHash = "FFFF" }];
            Assert.Equal(resolved, await Request(2));
            string moved = Path.Combine(root, "moved.ttf");
            File.Move(fontPath, moved);
            selected = selected with { Path = moved, FaceId = 3 };
            faces = [selected];
            Assert.Equal(resolved, await Request(2));
            bytes = [4, 5, 6];
            await File.WriteAllBytesAsync(moved, bytes, TestContext.Current.CancellationToken);
            selected = selected with { ContentHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) };
            faces = [selected];
            string changed = await Request(3);
            Assert.NotEqual(resolved, changed);
            Assert.Equal(changed, await Request(3));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Theory]
    [InlineData(SubtitleProcessingMode.Subsetting, ".ass")]
    [InlineData(SubtitleProcessingMode.GenerateMks, ".mks")]
    [InlineData(SubtitleProcessingMode.ConvertToSup, ".sup")]
    public async Task EveryModeLooksUpDependencyFingerprint(SubtitleProcessingMode mode, string extension)
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "source.ass");
            await File.WriteAllTextAsync(source, "[V4+ Styles]\nFormat: Name, Fontname, Fontsize, Bold, Italic\nStyle: Default,Example,20,0,0\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,A", TestContext.Current.CancellationToken);
            using var manager = FontCacheManagerFixture.CreateManager();
            var config = new PluginConfiguration { SubtitleMode = mode };
            using var service = new SubtitleCacheService(() => config, null!, null, null, Path.Combine(root, "cache"), NullLogger<SubtitleCacheService>.Instance, manager);
            Guid item = Guid.NewGuid();
            string fingerprint = Jellyfin.Plugin.AssSubsetter.Helpers.SubtitleCacheFingerprint.Create(source, mode, 1920, 1080, Math.Clamp(config.AssToSupFrameRate, 10, 60), manager.GetSubtitleDependencies(source).Fingerprint);
            string expected = Path.Combine(service.CacheFolderPath, $"{item:N}_{fingerprint}{extension}");
            await File.WriteAllTextAsync(expected, "cached", TestContext.Current.CancellationToken);
            var result = await service.GetOrGenerateSubtitleAsync(item, source, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(result.IsReady);
            Assert.Equal(expected, result.Path);
            Assert.Equal(0, service.ActiveBackgroundConversionCount);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task RealSelectionTracksOnlySelectedContentAndFace()
    {
        string source = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(source, "[V4+ Styles]\nFormat: Name, Fontname, Fontsize, Bold, Italic\nStyle: Default,Example,20,0,0\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,A", TestContext.Current.CancellationToken);
            IReadOnlyList<RuntimeFontFace> faces = [];
            var repository = new Mock<IFontRepository>();
            repository.Setup(r => r.LoadPublishedRuntimeFacesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => faces);
            using var manager = FontCacheManagerFixture.CreateManager(repository.Object);
            async Task<string> Identity()
            {
                await manager.LoadPublishedSnapshotAsync(null, TestContext.Current.CancellationToken);
                var dependencies = manager.GetSubtitleDependencies(source);
                Assert.Single(dependencies.Selections);
                return dependencies.Fingerprint;
            }

            string missing = await Identity();
            var selected = new RuntimeFontFace(1, "old.ttf", 0, "Example", "Regular", ["Example"], 400, 5, 0, false, false, "TrueType", 0, true) { ContentHash = "ABCD", UnicodeCoverage = [new Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.UnicodeRange(65, 65)] };
            faces = [selected];
            string present = await Identity();
            Assert.NotEqual(missing, present);
            faces = [selected, selected with { FaceId = 2, FamilyName = "Unrelated", Names = ["Unrelated"], ContentHash = "FFFF" }];
            Assert.Equal(present, await Identity());
            faces = [selected with { Path = "moved.ttf", FaceId = 3 }];
            Assert.Equal(present, await Identity());
            faces = [selected with { ContentHash = "EEEE" }];
            Assert.NotEqual(present, await Identity());
            faces = [selected with { FaceIndex = 1 }];
            Assert.NotEqual(present, await Identity());
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InputChangeDuringMuxDoesNotPublishOutput(bool fontChange)
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "source.ass");
            await File.WriteAllTextAsync(source, "[V4+ Styles]\nFormat: Name, Fontname, Fontsize, Bold, Italic\nStyle: Default,Example,20,0,0\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,A", TestContext.Current.CancellationToken);
            var config = new PluginConfiguration { SubtitleMode = SubtitleProcessingMode.GenerateMks, FallbackToOriginalOnError = true };
            IReadOnlyList<RuntimeFontFace> faces = [];
            var repository = new Mock<IFontRepository>();
            repository.Setup(r => r.LoadPublishedRuntimeFacesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => faces);
            using var manager = FontCacheManagerFixture.CreateManager(repository.Object);
            var processor = new AssProcessor(() => config, manager, new AssDocumentParser(), NullLogger<AssProcessor>.Instance);
            var muxer = new MksMuxer(NullLogger<MksMuxer>.Instance, () => "ffmpeg", async (info, token) =>
            {
                await File.WriteAllTextAsync(info.ArgumentList[^1], "output", token);
                if (fontChange)
                {
                    faces = [new RuntimeFontFace(1, "resolved.ttf", 0, "Example", "Regular", ["Example"], 400, 5, 0, false, false, "TrueType", 0, true) { ContentHash = "ABCD", UnicodeCoverage = [new Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.UnicodeRange(65, 65)] }];
                    await manager.LoadPublishedSnapshotAsync(null, token);
                }
                else
                {
                    await File.AppendAllTextAsync(source, "changed", token);
                }
                return 0;
            });
            using var service = new SubtitleCacheService(() => config, processor, null, muxer, Path.Combine(root, "cache"), NullLogger<SubtitleCacheService>.Instance, manager);
            var result = await service.GetOrGenerateSubtitleAsync(Guid.NewGuid(), source, cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(result.IsReady);
            Assert.Empty(Directory.GetFiles(service.CacheFolderPath));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
