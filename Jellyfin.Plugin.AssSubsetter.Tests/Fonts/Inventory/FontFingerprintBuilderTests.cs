using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Fixtures;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Inventory;

public sealed class FontFingerprintBuilderTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(20000, true)]
    public void BuildFace_ShouldPreserveCanonicalMappingByteFormat(int count, bool variations)
    {
        byte[] bytes = TestFontBuilder.Sfnt().WithName("Mapping")
            .WithCmapFormat12And14(0, (uint)(count - 1), 0xfe0f).Build();
        OpenTypeFace face = OpenTypeFontReader.Read(bytes).Faces[0];
        if (variations)
        {
            byte[] cmap = new byte[86];
            face.Tables[OpenTypeTag.FromString("cmap")].Data.Span.CopyTo(cmap);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(50), 38);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(61), 21);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(65), 29);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(69), 1);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(77), 1);
            cmap[83] = 1;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(84), 2);
            bytes = TestFontBuilder.Sfnt().WithName("Mapping").WithTable("cmap", cmap).Build();
            face = OpenTypeFontReader.Read(bytes).Faces[0];
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(count);
        for (uint index = 0; index < count; index++)
        {
            writer.Write(index);
            writer.Write((ushort)(index + 1));
        }

        writer.Write(variations ? 2 : 0);
        if (variations)
        {
            for (uint index = 0; index < 2; index++)
            {
                writer.Write(0xfe0fU);
                writer.Write(index);
                writer.Write((ushort)(index + 1));
            }
        }

        writer.Write(1); // One variation selector.
        writer.Write(0xfe0fU);
        byte[] canonical = stream.ToArray();
        byte[] hashInput = new byte[8 + canonical.Length];
        "cmap"u8.CopyTo(hashInput);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(hashInput.AsSpan(4), canonical.Length);
        canonical.CopyTo(hashInput, 8);

        FontFaceFingerprints result = FontFingerprintBuilder.BuildFace(face);

        Assert.True(result.IsSemanticEvidenceComplete);
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(hashInput), result.SemanticFingerprints[FontSemanticGroup.Mapping].ToArray());
    }

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
