using System.Buffers.Binary;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Fixtures;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Inventory;

public sealed class FontGlyphCanonicalizerTests
{
    [Fact]
    public void TryBuild_ShouldRetainLegacyCompositeBitWithoutLosingOutlineDistinction()
    {
        OpenTypeFace legacy = Face(0x0212);
        OpenTypeFace ordinary = Face(0x0202);

        Assert.True(FontGlyphCanonicalizer.TryBuild(legacy, out byte[] legacyOutline, out _, out string? error), error);
        Assert.True(FontGlyphCanonicalizer.TryBuild(ordinary, out byte[] ordinaryOutline, out _));
        Assert.NotEqual(ordinaryOutline, legacyOutline);
    }

    [Fact]
    public void TryBuild_ShouldStillRejectUnsupportedCompositeBitsWithSpecificGlyph()
    {
        Assert.False(FontGlyphCanonicalizer.TryBuild(Face(0x2002), out _, out _, out string? error));
        Assert.Contains("Glyph 1", error, StringComparison.Ordinal);
    }

    private static OpenTypeFace Face(ushort flags)
    {
        byte[] maxp = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(maxp.AsSpan(4, 2), 2);
        byte[] loca = new byte[6];
        BinaryPrimitives.WriteUInt16BigEndian(loca.AsSpan(4, 2), 8);
        byte[] glyf = new byte[16];
        BinaryPrimitives.WriteInt16BigEndian(glyf.AsSpan(0, 2), -1);
        BinaryPrimitives.WriteUInt16BigEndian(glyf.AsSpan(10, 2), flags);
        byte[] font = TestFontBuilder.Sfnt()
            .WithHead(0)
            .WithTable("maxp", maxp)
            .WithTable("loca", loca)
            .WithTable("glyf", glyf)
            .Build();
        return OpenTypeFontReader.Read(font).Faces[0];
    }
}
