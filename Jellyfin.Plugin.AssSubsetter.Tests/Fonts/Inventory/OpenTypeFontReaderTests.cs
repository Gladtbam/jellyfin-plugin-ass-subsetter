using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Fixtures;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Inventory;

public sealed class OpenTypeFontReaderTests
{
    [Fact]
    public void Read_ShouldRejectTableOutsideFileBounds()
    {
        byte[] corrupt = TestFontBuilder.Sfnt()
            .WithTable("name", [1, 2, 3, 4])
            .BuildWithOutOfBoundsTable("name");

        Assert.Throws<InvalidDataException>(() => OpenTypeFontReader.Read(corrupt));
    }

    [Fact]
    public void Read_ShouldExposeEveryTtcFaceWithItsOriginalIndex()
    {
        byte[] first = TestFontBuilder.Sfnt().WithTable("name", [1]).Build();
        byte[] second = TestFontBuilder.Sfnt().WithTable("name", [2]).Build();

        OpenTypeContainer container = OpenTypeFontReader.Read(TestFontBuilder.Collection(first, second));

        Assert.Equal([0, 1], container.Faces.Select(face => face.FaceIndex));
        Assert.Equal([1, 2], container.Faces.Select(face => face.Tables[OpenTypeTag.FromString("name")].Data.Span[0]));
    }

    [Fact]
    public void Read_ShouldRejectUnsupportedScalerSignature()
    {
        byte[] font = TestFontBuilder.Sfnt().WithTable("name", [1]).Build();
        font[0] = 0xde;
        font[1] = 0xad;
        font[2] = 0xbe;
        font[3] = 0xef;

        Assert.Throws<InvalidDataException>(() => OpenTypeFontReader.Read(font));
    }
}
