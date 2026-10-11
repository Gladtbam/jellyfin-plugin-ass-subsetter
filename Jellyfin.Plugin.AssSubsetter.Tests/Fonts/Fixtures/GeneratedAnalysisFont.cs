using System.Buffers.Binary;
using System.Text;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Fixtures;

internal static class GeneratedAnalysisFont
{
    internal const int GlyphCount = 5;

    internal static byte[] Create(byte[]? cmap = null, Action<SortedDictionary<string, byte[]>>? modify = null)
    {
        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["head"] = Head(),
            ["name"] = OpenTypeFontReader.Read(TestFontBuilder.Sfnt().WithNames((1, "Deterministic Subset Fixture"), (2, "Regular"), (6, "DeterministicFixture-Regular")).Build()).Faces[0].Tables[OpenTypeTag.FromString("name")].Data.ToArray(),
            ["cmap"] = cmap ?? Cmap(),
            ["maxp"] = Maxp(),
            ["hhea"] = Hhea(),
            ["hmtx"] = Hmtx(),
            ["loca"] = Loca(),
            ["glyf"] = Glyf(),
            ["OS/2"] = Os2(),
            ["post"] = Post(),
        };
        modify?.Invoke(tables);
        int directoryLength = 12 + (tables.Count * 16);
        int offset = Align4(directoryLength);
        foreach (byte[] table in tables.Values) { offset = Align4(offset + table.Length); }
        byte[] font = new byte[offset];
        BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(0, 4), 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(4, 2), (ushort)tables.Count);
        int maxPowerOfTwo = 1;
        while ((maxPowerOfTwo * 2) <= tables.Count) { maxPowerOfTwo *= 2; }
        BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(6, 2), (ushort)(maxPowerOfTwo * 16));
        BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(8, 2), (ushort)Math.Log2(maxPowerOfTwo));
        BinaryPrimitives.WriteUInt16BigEndian(font.AsSpan(10, 2), (ushort)((tables.Count * 16) - (maxPowerOfTwo * 16)));
        int recordOffset = 12;
        int tableOffset = Align4(directoryLength);
        foreach ((string tag, byte[] data) in tables)
        {
            Encoding.ASCII.GetBytes(tag, font.AsSpan(recordOffset, 4));
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(recordOffset + 4, 4), Checksum(data));
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(recordOffset + 8, 4), (uint)tableOffset);
            BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(recordOffset + 12, 4), (uint)data.Length);
            data.CopyTo(font, tableOffset);
            recordOffset += 16;
            tableOffset = Align4(tableOffset + data.Length);
        }

        int headOffset = BinaryPrimitives.ReadInt32BigEndian(font.AsSpan(12 + (Array.IndexOf(tables.Keys.ToArray(), "head") * 16) + 8, 4));
        BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(headOffset + 8, 4), unchecked(0xB1B0AFBAU - Checksum(font)));

        return font;
    }

    private static byte[] Head()
    {
        byte[] table = new byte[54];
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(0, 4), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(4, 4), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(12, 4), 0x5F0F3CF5);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(18, 2), 1000);
        BinaryPrimitives.WriteInt16BigEndian(table.AsSpan(50, 2), 1);
        return table;
    }

    private static byte[] Name()
    {
        byte[] text = Encoding.BigEndianUnicode.GetBytes("Deterministic Subset Fixture");
        byte[] table = new byte[18 + text.Length];
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(2, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(4, 2), 18);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(6, 2), 3);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(8, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(10, 2), 0x0409);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(12, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(14, 2), (ushort)text.Length);
        text.CopyTo(table, 18);
        return table;
    }

    private static byte[] Cmap()
    {
        byte[] table = new byte[40];
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(2, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(4, 2), 3);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(6, 2), 10);
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(8, 4), 12);
        BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(12, 2), 12);
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(16, 4), 28);
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(24, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(28, 4), 32);
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(32, 4), (uint)(GlyphCount + 30));
        BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(36, 4), 1);
        return table;
    }

    private static byte[] Maxp() { byte[] table = new byte[32]; BinaryPrimitives.WriteUInt32BigEndian(table, 0x00010000); BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(4, 2), GlyphCount); return table; }
    private static byte[] Hhea() { byte[] table = new byte[36]; BinaryPrimitives.WriteUInt32BigEndian(table, 0x00010000); BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(34, 2), GlyphCount); return table; }
    private static byte[] Hmtx() { byte[] table = new byte[GlyphCount * 4]; for (int i = 0; i < GlyphCount; i++) { BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(i * 4, 2), (ushort)(500 + (i * 10))); } return table; }
    private static byte[] Loca() { byte[] table = new byte[(GlyphCount + 1) * 4]; for (int i = 0; i <= GlyphCount; i++) { BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(i * 4, 4), (uint)(i * 10)); } return table; }
    private static byte[] Glyf() => new byte[GlyphCount * 10];
    private static byte[] Os2() { byte[] table = new byte[96]; BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(4, 2), 400); BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(6, 2), 5); BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(64, 2), 32); BinaryPrimitives.WriteUInt16BigEndian(table.AsSpan(66, 2), (ushort)(GlyphCount + 30)); return table; }
    private static byte[] Post() { byte[] table = new byte[32]; BinaryPrimitives.WriteUInt32BigEndian(table, 0x00030000); return table; }
    private static uint Checksum(byte[] data)
    {
        uint sum = 0;
        for (int offset = 0; offset < data.Length; offset += 4)
        {
            uint word = 0;
            for (int index = 0; index < 4 && offset + index < data.Length; index++) { word |= (uint)data[offset + index] << (24 - (index * 8)); }
            sum = unchecked(sum + word);
        }

        return sum;
    }

    private static int Align4(int value) => (value + 3) & ~3;
}
