using System.Buffers.Binary;
using Jellyfin.Plugin.AssSubsetter.Core;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Core;

public sealed class PgsEncoderTests
{
    [Fact]
    public void QuantizeToPalette_KeepsTransparentAndFirstSeenColors()
    {
        byte[] rgba = [255, 0, 0, 255, 1, 2, 3, 0, 0, 0, 255, 128, 255, 0, 0, 255];

        PgsEncoder.QuantizeToPalette(rgba, 4, 1, out byte[] indexed, out uint[] palette);

        Assert.Equal<byte>([1, 0, 2, 1], indexed);
        Assert.Equal<uint>([0, 0xFF0000FF, 0x0000FF80], palette);
    }

    [Fact]
    public void QuantizeToPalette_PreservesClosestIndicesAcrossRepeatedOverflowColors()
    {
        const int paletteColors = 255;
        const int overflowColors = 1280;
        byte[] indexedExpected = new byte[paletteColors + overflowColors + 3];
        byte[] rgba = new byte[indexedExpected.Length * sizeof(uint)];
        uint[] paletteExpected = new uint[paletteColors + 1];

        for (int i = 0; i < paletteColors; i++)
        {
            uint red = (uint)(i % 128 * 2);
            uint green = (uint)(64 + (i / 128 * 8));
            uint color = (red << 24) | (green << 16) | 255u;
            WritePixel(i, color);
            indexedExpected[i] = (byte)(i + 1);
            paletteExpected[i + 1] = color;
        }

        for (int i = 0; i < overflowColors; i++)
        {
            // Each red component is midway between two palette entries. The earlier entry wins.
            uint red = (i % 2 == 0) ? 1u : 3u;
            uint blue = (uint)(i % 256);
            uint alpha = (uint)(250 + (i / 256));
            WritePixel(paletteColors + i, (red << 24) | 0x00400000u | (blue << 8) | alpha);
            indexedExpected[paletteColors + i] = (byte)(i % 2 + 1);
        }

        WritePixel(paletteColors + overflowColors, 0x014000FA);
        WritePixel(paletteColors + overflowColors + 1, 0x0340FFFE);
        WritePixel(paletteColors + overflowColors + 2, 0xFFFFFF00);
        indexedExpected[^3] = 1;
        indexedExpected[^2] = 2;

        PgsEncoder.QuantizeToPalette(rgba, rgba.Length / sizeof(uint), 1, out byte[] indexed, out uint[] palette);

        Assert.Equal(indexedExpected, indexed);
        Assert.Equal(paletteExpected, palette);

        void WritePixel(int index, uint color) => BinaryPrimitives.WriteUInt32BigEndian(rgba.AsSpan(index * sizeof(uint)), color);
    }
}
