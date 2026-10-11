using System.Buffers.Binary;
using Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Fixtures;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Analysis;

public sealed class FontQualityVerifierTests
{
    [Fact]
    public void Verify_ShouldMarkAbsentOptionalCapabilitiesNotApplicable()
    {
        Dictionary<string, FontVerificationStatus> result = Verify(GeneratedAnalysisFont.Create());

        foreach (string capability in new[] { "layout", "vertical", "color", "axes" })
        {
            Assert.Equal(FontVerificationStatus.NotApplicable, result[capability]);
        }
    }

    [Fact]
    public void Verify_ShouldNotReportSignatureEvenWhenDsigIsPresent()
    {
        byte[] data = GeneratedAnalysisFont.Create(modify: tables => tables["DSIG"] = new byte[8]);

        Assert.DoesNotContain("signature", Verify(data).Keys);
    }

    [Fact]
    public void Verify_ShouldRejectTruncatedVerticalMetrics()
    {
        byte[] data = GeneratedAnalysisFont.Create(modify: tables =>
        {
            byte[] vhea = new byte[36];
            BinaryPrimitives.WriteUInt32BigEndian(vhea, 0x00010000);
            BinaryPrimitives.WriteUInt16BigEndian(vhea.AsSpan(34, 2), 2);
            tables["vhea"] = vhea;
            tables["vmtx"] = new byte[4];
        });

        Assert.Equal(FontVerificationStatus.Failed, Verify(data)["vertical"]);
    }

    [Theory]
    [InlineData(0x00010000U)]
    [InlineData(0x00011000U)]
    public void Verify_ShouldAcceptCompleteVerticalMetrics(uint version)
    {
        byte[] data = GeneratedAnalysisFont.Create(modify: tables =>
        {
            byte[] vhea = new byte[36];
            BinaryPrimitives.WriteUInt32BigEndian(vhea, version);
            BinaryPrimitives.WriteUInt16BigEndian(vhea.AsSpan(34, 2), 2);
            tables["vhea"] = vhea;
            tables["vmtx"] = new byte[14];
        });

        Assert.Equal(FontVerificationStatus.Passed, Verify(data)["vertical"]);
    }

    [Fact]
    public void Verify_ShouldResolveUnicodeMappingThroughFreeTypeWhenAvailable()
    {
        FontVerificationStatus result = Verify(GeneratedAnalysisFont.Create())["freetype"];

        Assert.Equal(
            FreeTypeFontVerifier.IsAvailable ? FontVerificationStatus.Passed : FontVerificationStatus.Unknown,
            result);
    }

    [Fact]
    public void Verify_ShouldRejectAnUnrenderableFixtureThroughLibassWhenAvailable()
    {
        FontVerificationStatus result = Verify(GeneratedAnalysisFont.Create())["libass"];

        Assert.Equal(
            LibassFontVerifier.IsAvailable ? FontVerificationStatus.Failed : FontVerificationStatus.Unknown,
            result);
    }

    [Fact]
    public void LibassProbe_ShouldNotAcceptAnInvalidAttachmentThroughFallback()
    {
        byte[] valid = GeneratedAnalysisFont.Create();
        OpenTypeFace face = OpenTypeFontReader.Read(valid).Faces[0];
        InspectedFontFace inspected = FontFaceInspector.Inspect(face);

        FontVerificationStatus result = LibassFontVerifier.Verify([1, 2, 3, 4], inspected);

        Assert.Equal(
            LibassFontVerifier.IsAvailable ? FontVerificationStatus.Failed : FontVerificationStatus.Unknown,
            result);
    }

    [Fact]
    public void LibassProbe_ShouldRenderARealFontOnLinux()
    {
        const string fontPath = "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";
        if (!OperatingSystem.IsLinux() || !File.Exists(fontPath))
        {
            return;
        }

        byte[] data = File.ReadAllBytes(fontPath);
        Assert.Equal(FontVerificationStatus.Passed, Verify(data)["libass"]);
    }

    [Fact]
    public void Verify_ShouldValidateSupportedLayoutDirectories()
    {
        byte[] valid = GeneratedAnalysisFont.Create(modify: tables =>
        {
            byte[] gsub = new byte[16];
            BinaryPrimitives.WriteUInt32BigEndian(gsub, 0x00010000);
            BinaryPrimitives.WriteUInt16BigEndian(gsub.AsSpan(4, 2), 10);
            BinaryPrimitives.WriteUInt16BigEndian(gsub.AsSpan(6, 2), 12);
            BinaryPrimitives.WriteUInt16BigEndian(gsub.AsSpan(8, 2), 14);
            tables["GSUB"] = gsub;
        });
        byte[] broken = GeneratedAnalysisFont.Create(modify: tables => tables["GSUB"] = new byte[10]);

        Assert.Equal(FontVerificationStatus.Passed, Verify(valid)["layout"]);
        Assert.Equal(FontVerificationStatus.Failed, Verify(broken)["layout"]);
    }

    [Fact]
    public void Verify_ShouldValidateSingleSubstitutionAndWithholdCreditForOtherLookups()
    {
        byte[] valid = GeneratedAnalysisFont.Create(modify: tables => tables["GSUB"] = SingleSubstitutionTable(1, 34));
        byte[] broken = GeneratedAnalysisFont.Create(modify: tables => tables["GSUB"] = SingleSubstitutionTable(1, 0xFFFF));
        byte[] unsupported = GeneratedAnalysisFont.Create(modify: tables => tables["GSUB"] = SingleSubstitutionTable(2, 34));
        byte[] overlappingMarkFilter = GeneratedAnalysisFont.Create(modify: tables =>
        {
            byte[] table = SingleSubstitutionTable(1, 34);
            BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(20, 2), 0x0010);
            tables["GSUB"] = table;
        });

        Assert.Equal(FontVerificationStatus.Passed, Verify(valid)["layout"]);
        Assert.Equal(FontVerificationStatus.Failed, Verify(broken)["layout"]);
        Assert.Equal(FontVerificationStatus.Unknown, Verify(unsupported)["layout"]);
        Assert.Equal(FontVerificationStatus.Failed, Verify(overlappingMarkFilter)["layout"]);
    }

    [Fact]
    public void Verify_ShouldValidateColorLayersAndPaletteReferences()
    {
        byte[] valid = GeneratedAnalysisFont.Create(modify: tables => AddColorTables(tables, 0));
        byte[] broken = GeneratedAnalysisFont.Create(modify: tables => AddColorTables(tables, 2));
        byte[] overlapping = GeneratedAnalysisFont.Create(modify: tables =>
        {
            AddColorTables(tables, 0);
            BinaryPrimitives.WriteUInt32BigEndian(tables["COLR"].AsSpan(8, 4), 16);
        });

        Assert.Equal(FontVerificationStatus.Passed, Verify(valid)["color"]);
        Assert.Equal(FontVerificationStatus.Failed, Verify(broken)["color"]);
        Assert.Equal(FontVerificationStatus.Failed, Verify(overlapping)["color"]);
    }

    [Fact]
    public void Verify_ShouldValidateVariableAxisAndGlyphOffsets()
    {
        byte[] valid = GeneratedAnalysisFont.Create(modify: tables => AddVariationTables(tables, 44));
        byte[] broken = GeneratedAnalysisFont.Create(modify: tables => AddVariationTables(tables, 100));

        Assert.Equal(FontVerificationStatus.Passed, Verify(valid)["axes"]);
        Assert.Equal(FontVerificationStatus.Failed, Verify(broken)["axes"]);
    }

    [Fact]
    public void Verify_ShouldWithholdAxisCompletenessWhenGlyphDeltasAreNotDecoded()
    {
        byte[] data = GeneratedAnalysisFont.Create(modify: tables =>
        {
            AddVariationTables(tables, 44);
            byte[] gvar = new byte[46];
            tables["gvar"].CopyTo(gvar, 0);
            BinaryPrimitives.WriteUInt32BigEndian(gvar.AsSpan(40, 4), 2);
            tables["gvar"] = gvar;
        });

        Assert.Equal(FontVerificationStatus.Unknown, Verify(data)["axes"]);
    }

    [Fact]
    public void Verify_ShouldRejectOversizedOptionalTableOffsetsWithoutThrowing()
    {
        byte[] color = GeneratedAnalysisFont.Create(modify: tables =>
        {
            AddColorTables(tables, 0);
            BinaryPrimitives.WriteUInt32BigEndian(tables["COLR"].AsSpan(4, 4), uint.MaxValue);
        });
        byte[] axes = GeneratedAnalysisFont.Create(modify: tables => AddVariationTables(tables, uint.MaxValue));

        Assert.Equal(FontVerificationStatus.Failed, Verify(color)["color"]);
        Assert.Equal(FontVerificationStatus.Failed, Verify(axes)["axes"]);
    }

    private static void AddColorTables(SortedDictionary<string, byte[]> tables, ushort paletteIndex)
    {
        byte[] colr = new byte[24];
        BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(2, 2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(4, 4), 14);
        BinaryPrimitives.WriteUInt32BigEndian(colr.AsSpan(8, 4), 20);
        BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(12, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(14, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(18, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(20, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(colr.AsSpan(22, 2), paletteIndex);
        byte[] cpal = new byte[18];
        BinaryPrimitives.WriteUInt16BigEndian(cpal.AsSpan(2, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(cpal.AsSpan(4, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(cpal.AsSpan(6, 2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(cpal.AsSpan(8, 4), 14);
        cpal[17] = 255;
        tables["COLR"] = colr;
        tables["CPAL"] = cpal;
    }

    private static void AddVariationTables(SortedDictionary<string, byte[]> tables, uint dataOffset)
    {
        OpenTypeFace axisFace = OpenTypeFontReader.Read(TestFontBuilder.Sfnt()
            .WithSingleVariationAxis("wght", 100, 400, 900).Build()).Faces[0];
        tables["fvar"] = axisFace.Tables[OpenTypeTag.FromString("fvar")].Data.ToArray();
        byte[] gvar = new byte[44];
        BinaryPrimitives.WriteUInt32BigEndian(gvar, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(gvar.AsSpan(4, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(gvar.AsSpan(12, 2), GeneratedAnalysisFont.GlyphCount);
        BinaryPrimitives.WriteUInt16BigEndian(gvar.AsSpan(14, 2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(gvar.AsSpan(16, 4), dataOffset);
        tables["gvar"] = gvar;
    }

    private static byte[] SingleSubstitutionTable(ushort lookupType, ushort coverageOffset)
    {
        byte[] table = new byte[40];
        BinaryPrimitives.WriteUInt32BigEndian(table, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(4, 2), 10);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(6, 2), 12);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(8, 2), 14);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(14, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(16, 2), 4);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(18, 2), lookupType);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(24, 2), 8);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(26, 2), 2);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(28, 2), coverageOffset == 34 ? (ushort)8 : coverageOffset);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(30, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(32, 2), 2);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(34, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(36, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(38, 2), 1);
        return table;
    }

    private static Dictionary<string, FontVerificationStatus> Verify(byte[] data)
    {
        OpenTypeFace face = OpenTypeFontReader.Read(data).Faces[0];
        InspectedFontFace inspected = FontFaceInspector.Inspect(face);
        Assert.True(inspected.IsClassifiable);
        return FontQualityVerifier.Verify(data, face, inspected, true);
    }
}
