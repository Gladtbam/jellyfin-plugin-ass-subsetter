using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Fixtures;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Inventory;

public sealed class FontFingerprintBuilderTests
{
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
