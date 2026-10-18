using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Fixtures;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Inventory;

public sealed class FontFingerprintBuilderTests
{
    [Fact]
    public void BuildFace_ShouldNotAllocateInProportionToUnmodifiedTableSize()
    {
        byte[] bytes = TestFontBuilder.Sfnt().WithTable("GPOS", new byte[1024 * 1024]).Build();
        OpenTypeFace face = OpenTypeFontReader.Read(bytes).Faces[0];
        FontFaceFingerprints expected = FontFingerprintBuilder.BuildFace(face);

        long before = GC.GetAllocatedBytesForCurrentThread();
        FontFaceFingerprints actual = FontFingerprintBuilder.BuildFace(face);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(expected.StrictFingerprint.ToArray(), actual.StrictFingerprint.ToArray());
        Assert.Equal(actual.StrictFingerprint.ToArray(), actual.SemanticFingerprints[FontSemanticGroup.Layout].ToArray());
        Assert.True(allocated < 64 * 1024, $"Fingerprinting allocated {allocated} bytes for an unchanged table.");
    }

    [Fact]
    public void BuildFace_ShouldPreserveInputWhenNormalizingMetadata()
    {
        byte[] bytes = TestFontBuilder.Sfnt().WithHead(123)
            .WithStyleMetadata(400, 5, false, false).WithName("Example").Build();
        byte[] original = bytes.ToArray();
        OpenTypeFace face = OpenTypeFontReader.Read(bytes).Faces[0];

        FontFaceFingerprints first = FontFingerprintBuilder.BuildFace(face);
        FontFaceFingerprints second = FontFingerprintBuilder.BuildFace(face);

        Assert.Equal(original, bytes);
        Assert.Equal(first.StrictFingerprint.ToArray(), second.StrictFingerprint.ToArray());
        foreach (var group in first.SemanticFingerprints)
        {
            Assert.Equal(group.Value.ToArray(), second.SemanticFingerprints[group.Key].ToArray());
        }
    }

    [Fact]
    public void BuildFace_ShouldIgnoreContainerOffsetsAndHeadChecksumAdjustment()
    {
        byte[] first = TestFontBuilder.Sfnt().WithHead(1).WithName("Example").Build();
        byte[] second = TestFontBuilder.Sfnt().WithHead(9).WithName("Example").PadTablesBy(32).Build();

        FontFaceFingerprints firstFingerprint = FontFingerprintBuilder.BuildFace(OpenTypeFontReader.Read(first).Faces[0]);
        FontFaceFingerprints secondFingerprint = FontFingerprintBuilder.BuildFace(OpenTypeFontReader.Read(second).Faces[0]);

        Assert.Equal(firstFingerprint.StrictFingerprint.ToArray(), secondFingerprint.StrictFingerprint.ToArray());
    }

    [Fact]
    public void BuildFace_ShouldSeparateMetadataFromEqualLayoutEvidence()
    {
        byte[] first = TestFontBuilder.Sfnt().WithName("First").WithTable("GPOS", [1, 2, 3]).Build();
        byte[] second = TestFontBuilder.Sfnt().WithName("Second").WithTable("GPOS", [1, 2, 3]).Build();

        FontFaceFingerprints firstFingerprint = FontFingerprintBuilder.BuildFace(OpenTypeFontReader.Read(first).Faces[0]);
        FontFaceFingerprints secondFingerprint = FontFingerprintBuilder.BuildFace(OpenTypeFontReader.Read(second).Faces[0]);

        Assert.NotEqual(firstFingerprint.StrictFingerprint.ToArray(), secondFingerprint.StrictFingerprint.ToArray());
        Assert.NotEqual(
            firstFingerprint.SemanticFingerprints[FontSemanticGroup.Metadata].ToArray(),
            secondFingerprint.SemanticFingerprints[FontSemanticGroup.Metadata].ToArray());
        Assert.Equal(
            firstFingerprint.SemanticFingerprints[FontSemanticGroup.Layout].ToArray(),
            secondFingerprint.SemanticFingerprints[FontSemanticGroup.Layout].ToArray());
    }

    [Fact]
    public void BuildFace_ShouldMatchCorrespondingFacesAcrossCollectionContainers()
    {
        byte[] face = TestFontBuilder.Sfnt().WithHead(123).WithName("Collection Face").Build();
        OpenTypeFace standalone = OpenTypeFontReader.Read(face).Faces[0];
        OpenTypeFace collected = OpenTypeFontReader.Read(TestFontBuilder.Collection(face)).Faces[0];

        FontFaceFingerprints standaloneFingerprint = FontFingerprintBuilder.BuildFace(standalone);
        FontFaceFingerprints collectedFingerprint = FontFingerprintBuilder.BuildFace(collected);

        Assert.Equal(
            standaloneFingerprint.StrictFingerprint.ToArray(),
            collectedFingerprint.StrictFingerprint.ToArray());
    }
}
