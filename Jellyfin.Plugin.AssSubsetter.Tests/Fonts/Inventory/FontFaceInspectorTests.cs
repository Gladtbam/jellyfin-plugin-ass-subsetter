using System.Buffers.Binary;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Fixtures;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Inventory;

public sealed class FontFaceInspectorTests
{
    [Fact]
    public void Inspect_ShouldReadDefaultAndNonDefaultVariationMappings()
    {
        // Default A+FE0F uses the ordinary glyph; non-default B+FE0F uses glyph 3.
        byte[] cmap = [0, 0, 0, 2, 0, 3, 0, 10, 0, 0, 0, 20, 0, 0, 0, 5, 0, 0, 0, 48,
            0, 12, 0, 0, 0, 0, 0, 28, 0, 0, 0, 0, 0, 0, 0, 1, 0, 0, 0, 65, 0, 0, 0, 66, 0, 0, 0, 2,
            0, 14, 0, 0, 0, 38, 0, 0, 0, 1, 0, 254, 15, 0, 0, 0, 21, 0, 0, 0, 29,
            0, 0, 0, 1, 0, 0, 65, 0, 0, 0, 0, 1, 0, 0, 66, 0, 3];
        byte[] font = TestFontBuilder.Sfnt().WithName("UVS Fixture").WithTable("cmap", cmap).Build();

        InspectedFontFace inspected = FontFaceInspector.Inspect(OpenTypeFontReader.Read(font).Faces[0]);

        Assert.True(inspected.IsClassifiable, inspected.Error);
        Assert.Collection(inspected.VariationMappings,
            mapping => Assert.Equal(new UnicodeVariationMapping(0x41, 0xfe0f, null), mapping),
            mapping => Assert.Equal(new UnicodeVariationMapping(0x42, 0xfe0f, 3), mapping));
    }

    [Fact]
    public void Inspect_ShouldRefuseMalformedVariationEvidenceEvenWithValidOrdinaryCmap()
    {
        byte[] font = TestFontBuilder.Sfnt().WithName("UVS Fixture").WithCmapFormat12And14(0x41, 0x42, 0xfe0f).Build();
        OpenTypeFace face = OpenTypeFontReader.Read(font).Faces[0];
        OpenTypeTable cmap = face.Tables[OpenTypeTag.FromString("cmap")];
        font[cmap.Offset + 68] = 255; // non-default UVS offset points beyond format 14.

        InspectedFontFace inspected = FontFaceInspector.Inspect(OpenTypeFontReader.Read(font).Faces[0]);

        Assert.False(inspected.IsClassifiable);
    }

    [Fact]
    public void Inspect_ShouldExtractLookupStyleCoverageAndVariationEvidence()
    {
        byte[] font = TestFontBuilder.Sfnt()
            .WithHead(0)
            .WithName("Example Sans")
            .WithStyleMetadata(weight: 650, width: 7, italic: true, fixedPitch: true)
            .WithCmapFormat12(0x1f600, 0x1f601)
            .WithSingleVariationAxis("wght", 100, 400, 900)
            .Build();

        InspectedFontFace inspected = FontFaceInspector.Inspect(OpenTypeFontReader.Read(font).Faces[0]);

        Assert.True(inspected.IsClassifiable);
        Assert.Equal("Example Sans", inspected.FamilyName);
        Assert.Equal(650, inspected.Weight);
        Assert.Equal(7, inspected.Width);
        Assert.True(inspected.IsItalic);
        Assert.True(inspected.IsFixedPitch);
        Assert.Equal("0", inspected.HeadRevision);
        Assert.Equal("TEST", inspected.VendorId);
        Assert.Contains(inspected.UnicodeCoverage, range => range.Start == 0x1f600 && range.End == 0x1f601);
        FontVariationAxis axis = Assert.Single(inspected.VariationAxes);
        Assert.Equal("wght", OpenTypeTag.ToString(axis.Tag));
        Assert.Equal(400, axis.DefaultValue);
    }

    [Fact]
    public void Inspect_ShouldPreferTypographicNamesAndRecordCompletenessSignals()
    {
        byte[] font = TestFontBuilder.Sfnt()
            .WithNames((1, "Legacy Family"), (2, "Regular"), (6, "ExamplePS"), (16, "Typographic Family"), (17, "Display"))
            .WithCmapFormat12And14(0x41, 0x42, 0xfe0f)
            .WithTable("vhea", [])
            .WithTable("vmtx", [])
            .WithTable("COLR", [])
            .WithTable("GPOS", [])
            .Build();

        InspectedFontFace inspected = FontFaceInspector.Inspect(OpenTypeFontReader.Read(font).Faces[0]);

        Assert.Equal("Typographic Family", inspected.FamilyName);
        Assert.Equal("Display", inspected.StyleName);
        Assert.Contains("ExamplePS", inspected.Names);
        Assert.Equal([0xfe0fU], inspected.VariationSelectors);
        Assert.True(inspected.HasVerticalMetrics);
        Assert.True(inspected.HasLayoutTables);
        Assert.True(inspected.HasColorOrBitmapGlyphs);
        Assert.Equal(2, inspected.ValidCmapCount);
    }

    [Fact]
    public void Inspect_ShouldReadBmpCoverageFromCmapFormat4()
    {
        byte[] font = TestFontBuilder.Sfnt()
            .WithName("BMP Example")
            .WithCmapFormat4(0x20, 0x7e)
            .Build();

        InspectedFontFace inspected = FontFaceInspector.Inspect(OpenTypeFontReader.Read(font).Faces[0]);

        Assert.True(inspected.IsClassifiable);
        Assert.Contains(inspected.UnicodeCoverage, range => range.Start == 0x20 && range.End == 0x7e);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Inspect_ShouldIgnoreValidMacintoshCmapButRejectInvalidRecordOffset(bool invalidMacOffset)
    {
        byte[] unicodeFont = TestFontBuilder.Sfnt().WithCmapFormat12(0x41, 0x42).Build();
        byte[] unicodeCmap = OpenTypeFontReader.Read(unicodeFont).Faces[0]
            .Tables[OpenTypeTag.FromString("cmap")].Data.ToArray();
        byte[] cmap = new byte[58];
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(2, 2), 2);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(4, 2), 3);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(6, 2), 10);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(8, 4), 20);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(12, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(14, 2), 2);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(16, 4), invalidMacOffset ? uint.MaxValue : 48);
        unicodeCmap.AsSpan(12, 28).CopyTo(cmap.AsSpan(20));
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(48, 2), 6);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(50, 2), 10);
        byte[] font = TestFontBuilder.Sfnt().WithName("Unicode and Macintosh")
            .WithTable("cmap", cmap).Build();
        OpenTypeFace face = OpenTypeFontReader.Read(font).Faces[0];

        InspectedFontFace inspected = FontFaceInspector.Inspect(face);

        Assert.True(inspected.IsClassifiable, inspected.Error);
        Assert.Equal(!invalidMacOffset, inspected.IsMappingComplete);
        Assert.Equal(1, inspected.ValidCmapCount);
        Assert.Equal(!invalidMacOffset, FontFingerprintBuilder.BuildFace(face).IsSemanticEvidenceComplete);
    }

    [Fact]
    public void Inspect_ShouldRefuseClassificationWhenCmapEvidenceIsMalformed()
    {
        byte[] font = TestFontBuilder.Sfnt()
            .WithName("Broken Example")
            .WithTable("cmap", [0, 0, 0, 1])
            .Build();

        InspectedFontFace inspected = FontFaceInspector.Inspect(OpenTypeFontReader.Read(font).Faces[0]);

        Assert.False(inspected.IsClassifiable);
        Assert.Empty(inspected.UnicodeCoverage);
        Assert.NotNull(inspected.Error);
        Assert.NotEmpty(inspected.Error);
    }
    [Fact]
    public void Inspect_ShouldExplainLegacyOnlyCmap()
    {
        byte[] font = TestFontBuilder.Sfnt().WithName("Legacy Example").WithCmapFormat4(0x20, 0x7e).Build();
        OpenTypeTable cmap = OpenTypeFontReader.Read(font).Faces[0].Tables[OpenTypeTag.FromString("cmap")];
        BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(cmap.Offset + 6, 2), 3); // Windows PRC encoding, not Unicode.

        InspectedFontFace inspected = FontFaceInspector.Inspect(OpenTypeFontReader.Read(font).Faces[0]);

        Assert.False(inspected.IsClassifiable);
        Assert.Contains("legacy cmap", inspected.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Inspect_ShouldExposeMalformedUnicodeSubtableReason()
    {
        byte[] font = TestFontBuilder.Sfnt().WithName("Malformed Example").WithCmapFormat4(0x20, 0x7e).Build();
        OpenTypeTable cmap = OpenTypeFontReader.Read(font).Faces[0].Tables[OpenTypeTag.FromString("cmap")];
        int subtableOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(font.AsSpan(cmap.Offset + 8, 4));
        BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(cmap.Offset + subtableOffset + 2, 2), 18);

        InspectedFontFace inspected = FontFaceInspector.Inspect(OpenTypeFontReader.Read(font).Faces[0]);

        Assert.False(inspected.IsClassifiable);
        Assert.Contains("cmap format 4", inspected.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_ShouldIdentifyFormat4ReferenceBeyondDeclaredSubtableLength()
    {
        byte[] source = TestFontBuilder.Sfnt().WithName("Short Subtable").WithCmapFormat4(0x20, 0x20).Build();
        OpenTypeTable sourceCmap = OpenTypeFontReader.Read(source).Faces[0].Tables[OpenTypeTag.FromString("cmap")];
        byte[] cmap = new byte[sourceCmap.Length + 4];
        sourceCmap.Data.Span.CopyTo(cmap);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(40, 2), 4);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(44, 2), 1);
        byte[] font = TestFontBuilder.Sfnt().WithName("Short Subtable").WithTable("cmap", cmap).Build();

        InspectedFontFace inspected = FontFaceInspector.Inspect(OpenTypeFontReader.Read(font).Faces[0]);

        Assert.False(inspected.IsClassifiable);
        Assert.Equal("The cmap format 4 glyph array reference lies outside the declared subtable length.", inspected.Error);
    }
}
