using System.Buffers.Binary;
using System.Text;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Fixtures;

internal sealed class TestFontBuilder
{
    private readonly SortedDictionary<string, byte[]> _tables = new(StringComparer.Ordinal);
    private int _tablePadding;

    public static TestFontBuilder Sfnt() => new();

    public TestFontBuilder WithHead(uint checksumAdjustment)
    {
        byte[] data = new byte[54];
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8, 4), checksumAdjustment);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(18, 2), 1000);
        _tables["head"] = data;
        return this;
    }

    public TestFontBuilder WithName(string value)
    {
        return WithNames((1, value));
    }

    public TestFontBuilder WithNames(params (ushort NameId, string Value)[] names)
    {
        byte[][] encoded = names.Select(name => Encoding.BigEndianUnicode.GetBytes(name.Value)).ToArray();
        int recordsLength = checked(names.Length * 12);
        int stringOffset = checked(6 + recordsLength);
        byte[] data = new byte[checked(stringOffset + encoded.Sum(value => value.Length))];
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2, 2), checked((ushort)names.Length));
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4, 2), checked((ushort)stringOffset));
        int relativeOffset = 0;
        for (int index = 0; index < names.Length; index++)
        {
            int recordOffset = 6 + (index * 12);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(recordOffset, 2), 3);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(recordOffset + 2, 2), 1);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(recordOffset + 4, 2), 0x0409);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(recordOffset + 6, 2), names[index].NameId);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(recordOffset + 8, 2), checked((ushort)encoded[index].Length));
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(recordOffset + 10, 2), checked((ushort)relativeOffset));
            encoded[index].CopyTo(data, stringOffset + relativeOffset);
            relativeOffset += encoded[index].Length;
        }

        _tables["name"] = data;
        return this;
    }

    public TestFontBuilder WithStyleMetadata(int weight, int width, bool italic, bool fixedPitch)
    {
        byte[] os2 = new byte[64];
        BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(4, 2), checked((ushort)weight));
        BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(6, 2), checked((ushort)width));
        "TEST"u8.CopyTo(os2.AsSpan(58, 4));
        BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(62, 2), italic ? (ushort)1 : (ushort)0);
        _tables["OS/2"] = os2;
        byte[] post = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(post.AsSpan(12, 4), fixedPitch ? 1U : 0U);
        _tables["post"] = post;
        return this;
    }

    public TestFontBuilder WithCmapFormat12(uint startCodePoint, uint endCodePoint)
    {
        byte[] cmap = new byte[40];
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(2, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(4, 2), 3);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(6, 2), 10);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(8, 4), 12);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(12, 2), 12);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(16, 4), 28);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(24, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(28, 4), startCodePoint);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(32, 4), endCodePoint);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(36, 4), 1);
        _tables["cmap"] = cmap;
        return this;
    }

    public TestFontBuilder WithCmapFormat4(ushort startCodePoint, ushort endCodePoint)
    {
        byte[] cmap = new byte[44];
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(2, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(4, 2), 3);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(6, 2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(8, 4), 12);

        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(12, 2), 4);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(14, 2), 32);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(18, 2), 4);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(20, 2), 4);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(22, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(24, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(26, 2), endCodePoint);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(28, 2), 0xffff);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(32, 2), startCodePoint);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(34, 2), 0xffff);
        BinaryPrimitives.WriteInt16BigEndian(cmap.AsSpan(36, 2), 1);
        BinaryPrimitives.WriteInt16BigEndian(cmap.AsSpan(38, 2), 1);
        _tables["cmap"] = cmap;
        return this;
    }

    public TestFontBuilder WithCmapFormat12And14(uint startCodePoint, uint endCodePoint, uint variationSelector)
    {
        byte[] cmap = new byte[69];
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(2, 2), 2);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(4, 2), 3);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(6, 2), 10);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(8, 4), 20);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(12, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(14, 2), 5);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(16, 4), 48);

        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(20, 2), 12);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(24, 4), 28);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(32, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(36, 4), startCodePoint);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(40, 4), endCodePoint);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(44, 4), 1);

        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(48, 2), 14);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(50, 4), 21);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(54, 4), 1);
        cmap[58] = (byte)((variationSelector >> 16) & 0xff);
        cmap[59] = (byte)((variationSelector >> 8) & 0xff);
        cmap[60] = (byte)(variationSelector & 0xff);
        _tables["cmap"] = cmap;
        return this;
    }

    public TestFontBuilder WithSingleVariationAxis(string tag, float minimum, float defaultValue, float maximum)
    {
        byte[] fvar = new byte[36];
        BinaryPrimitives.WriteUInt16BigEndian(fvar.AsSpan(0, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(fvar.AsSpan(4, 2), 16);
        BinaryPrimitives.WriteUInt16BigEndian(fvar.AsSpan(8, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(fvar.AsSpan(10, 2), 20);
        Encoding.ASCII.GetBytes(tag, fvar.AsSpan(16, 4));
        BinaryPrimitives.WriteInt32BigEndian(fvar.AsSpan(20, 4), ToFixed(minimum));
        BinaryPrimitives.WriteInt32BigEndian(fvar.AsSpan(24, 4), ToFixed(defaultValue));
        BinaryPrimitives.WriteInt32BigEndian(fvar.AsSpan(28, 4), ToFixed(maximum));
        BinaryPrimitives.WriteUInt16BigEndian(fvar.AsSpan(34, 2), 256);
        _tables["fvar"] = fvar;
        return this;
    }

    public TestFontBuilder WithTable(string tag, byte[] data)
    {
        ArgumentException.ThrowIfNullOrEmpty(tag);
        ArgumentOutOfRangeException.ThrowIfNotEqual(tag.Length, 4);
        _tables[tag] = data;
        return this;
    }

    public TestFontBuilder PadTablesBy(int bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        _tablePadding = bytes;
        return this;
    }

    public byte[] Build()
    {
        int directoryLength = checked(12 + (_tables.Count * 16));
        int dataOffset = Align4(checked(directoryLength + _tablePadding));
        int length = dataOffset;
        foreach (byte[] table in _tables.Values)
        {
            length = checked(Align4(checked(length + table.Length)));
        }

        byte[] result = new byte[length];
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(0, 4), 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(4, 2), checked((ushort)_tables.Count));
        int recordOffset = 12;
        int tableOffset = dataOffset;
        foreach ((string tag, byte[] data) in _tables)
        {
            Encoding.ASCII.GetBytes(tag, result.AsSpan(recordOffset, 4));
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(recordOffset + 8, 4), checked((uint)tableOffset));
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(recordOffset + 12, 4), checked((uint)data.Length));
            data.CopyTo(result, tableOffset);
            recordOffset += 16;
            tableOffset = Align4(checked(tableOffset + data.Length));
        }

        return result;
    }

    public byte[] BuildWithOutOfBoundsTable(string tag)
    {
        byte[] result = Build();
        int recordOffset = FindTableRecord(result, tag);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(recordOffset + 8, 4), checked((uint)(result.Length - 1)));
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(recordOffset + 12, 4), 32);
        return result;
    }

    public static byte[] Collection(params byte[][] fonts)
    {
        int headerLength = checked(12 + (fonts.Length * 4));
        int length = Align4(headerLength);
        foreach (byte[] font in fonts)
        {
            length = checked(Align4(checked(length + font.Length)));
        }

        byte[] result = new byte[length];
        Encoding.ASCII.GetBytes("ttcf", result.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(4, 4), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(8, 4), checked((uint)fonts.Length));
        int fontOffset = Align4(headerLength);
        for (int index = 0; index < fonts.Length; index++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(result.AsSpan(12 + (index * 4), 4), checked((uint)fontOffset));
            fonts[index].CopyTo(result, fontOffset);
            PatchTableOffsets(result.AsSpan(fontOffset, fonts[index].Length), fontOffset);
            fontOffset = Align4(checked(fontOffset + fonts[index].Length));
        }

        return result;
    }

    private static void PatchTableOffsets(Span<byte> sfnt, int baseOffset)
    {
        ushort tableCount = BinaryPrimitives.ReadUInt16BigEndian(sfnt.Slice(4, 2));
        for (int index = 0; index < tableCount; index++)
        {
            int recordOffset = 12 + (index * 16);
            uint relative = BinaryPrimitives.ReadUInt32BigEndian(sfnt.Slice(recordOffset + 8, 4));
            BinaryPrimitives.WriteUInt32BigEndian(sfnt.Slice(recordOffset + 8, 4), checked(relative + (uint)baseOffset));
        }
    }

    private static int FindTableRecord(byte[] sfnt, string tag)
    {
        ushort tableCount = BinaryPrimitives.ReadUInt16BigEndian(sfnt.AsSpan(4, 2));
        for (int index = 0; index < tableCount; index++)
        {
            int offset = 12 + (index * 16);
            if (Encoding.ASCII.GetString(sfnt, offset, 4) == tag)
            {
                return offset;
            }
        }

        throw new InvalidOperationException("The requested table does not exist.");
    }

    private static int Align4(int value) => checked((value + 3) & ~3);

    private static int ToFixed(float value) => checked((int)MathF.Round(value * 65536));
}
