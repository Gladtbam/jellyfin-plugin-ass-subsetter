using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.Core;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;
using Jellyfin.Plugin.AssSubsetter.Models;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Core;

public sealed class AssProcessorAttachmentTests
{
    [Fact]
    public async Task GenerateSubsetArtifactAsync_ShouldGiveEachStyleUniqueAttachmentNameAndKeepSharedFamily()
    {
        string root = Path.Join(Path.GetTempPath(), "AssProcessorAttachmentTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string fontPath = Path.Join(root, "source.ttf");
            using SKStreamAsset stream = SKTypeface.Default.OpenStream();
            using SKData data = SKData.Create(stream);
            await File.WriteAllBytesAsync(fontPath, data.ToArray(), TestContext.Current.CancellationToken);
            string input = Path.Join(root, "input.ass");
            const string content = "[V4+ Styles]\nFormat: Name, Fontname, Fontsize, Bold, Italic\nStyle: Regular,Example,20,0,0\nStyle: Bold,Example,20,-1,0\nStyle: Italic,Example,20,0,-1\n[Events]\nFormat: Layer, Start, End, Style, Text\nDialogue: 0,0:00:00.00,0:00:01.00,Regular,A\nDialogue: 0,0:00:00.00,0:00:01.00,Bold,B\nDialogue: 0,0:00:00.00,0:00:01.00,Italic,C";
            await File.WriteAllTextAsync(input, content, TestContext.Current.CancellationToken);
            var processor = new AssProcessor(
                () => new PluginConfiguration(),
                (_, _, _) => Task.FromResult<FontFileLease?>(new FontFileLease(fontPath, 0, () => ValueTask.CompletedTask)),
                () => "SHAREDFAMILY",
                new AssDocumentParser(),
                NullLogger<AssProcessor>.Instance);

            SubsetArtifact? artifact = await processor.GenerateSubsetArtifactAsync(input, TestContext.Current.CancellationToken);

            Assert.NotNull(artifact);
            Assert.Equal(3, artifact.Fonts.Count);
            Assert.Equal(3, artifact.Fonts.Select(font => font.FileName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
            foreach (SubsetFontAttachment font in artifact.Fonts)
            {
                Assert.EndsWith(".ttf", font.FileName, StringComparison.Ordinal);
                Assert.Equal("font/ttf", font.MimeType);
                using SKData subset = SKData.CreateCopy(font.Data);
                using SKTypeface typeface = SKTypeface.FromData(subset);
                Assert.NotNull(typeface);
                Assert.Equal("SHAREDFAMILY", typeface.FamilyName);
            }

            Assert.Contains("Style: Regular,SHAREDFAMILY,20,0,0", artifact.AssContent, StringComparison.Ordinal);
            Assert.Contains("Style: Bold,SHAREDFAMILY,20,-1,0", artifact.AssContent, StringComparison.Ordinal);
            Assert.Contains("Style: Italic,SHAREDFAMILY,20,0,-1", artifact.AssContent, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}
