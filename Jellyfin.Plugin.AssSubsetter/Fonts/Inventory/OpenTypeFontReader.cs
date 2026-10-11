using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

/// <summary>
/// Parses SFNT and TTC/OTC directories using checked arithmetic before exposing any table bytes.
/// </summary>
public static class OpenTypeFontReader
{
    private const uint TrueTypeCollectionTag = 0x74746366;
    private const uint TrueTypeScaler = 0x00010000;
    private const uint CffScaler = 0x4f54544f;
    private const uint AppleTrueScaler = 0x74727565;
    private const uint TypeOneScaler = 0x74797031;
    private const int MaximumFaceCount = 4096;
    private const int MaximumTableCount = 4096;

    /// <summary>Parses one complete font container from memory.</summary>
    /// <param name="data">The complete SFNT, TTC, or OTC bytes.</param>
    /// <returns>The validated container and its Face table directories.</returns>
    /// <exception cref="InvalidDataException">Thrown when a signature, count, offset, length, or duplicate tag is invalid.</exception>
    public static OpenTypeContainer Read(ReadOnlyMemory<byte> data)
    {
        EnsureRange(data.Length, 0, 4, "font signature");
        uint signature = BinaryPrimitives.ReadUInt32BigEndian(data.Span.Slice(0, 4));
        if (signature != TrueTypeCollectionTag)
        {
            return new OpenTypeContainer(false, [ReadFace(data, 0, 0)]);
        }

        EnsureRange(data.Length, 0, 12, "collection header");
        uint version = BinaryPrimitives.ReadUInt32BigEndian(data.Span.Slice(4, 4));
        if (version is not 0x00010000 and not 0x00020000)
        {
            throw new InvalidDataException("The TTC/OTC version is unsupported.");
        }

        uint rawFaceCount = BinaryPrimitives.ReadUInt32BigEndian(data.Span.Slice(8, 4));
        if (rawFaceCount is 0 or > MaximumFaceCount)
        {
            throw new InvalidDataException("The TTC/OTC Face count is outside the supported range.");
        }

        int faceCount = checked((int)rawFaceCount);
        int offsetsLength = CheckedMultiply(faceCount, 4, "collection Face offsets");
        EnsureRange(data.Length, 12, offsetsLength, "collection Face offsets");
        var faces = new List<OpenTypeFace>(faceCount);
        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            uint rawOffset = BinaryPrimitives.ReadUInt32BigEndian(data.Span.Slice(12 + (faceIndex * 4), 4));
            if (rawOffset > int.MaxValue)
            {
                throw new InvalidDataException("A TTC/OTC Face offset exceeds the supported address range.");
            }

            faces.Add(ReadFace(data, (int)rawOffset, faceIndex));
        }

        return new OpenTypeContainer(true, faces);
    }

    private static OpenTypeFace ReadFace(ReadOnlyMemory<byte> data, int directoryOffset, int faceIndex)
    {
        EnsureRange(data.Length, directoryOffset, 12, "SFNT offset table");
        ReadOnlySpan<byte> header = data.Span.Slice(directoryOffset, 12);
        uint scalerType = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(0, 4));
        if (scalerType is not TrueTypeScaler and not CffScaler and not AppleTrueScaler and not TypeOneScaler)
        {
            throw new InvalidDataException("The SFNT scaler signature is unsupported.");
        }

        ushort tableCount = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(4, 2));
        if (tableCount is 0 or > MaximumTableCount)
        {
            throw new InvalidDataException("The SFNT table count is outside the supported range.");
        }

        int tableDirectoryOffset = CheckedAdd(directoryOffset, 12, "SFNT table directory");
        int tableDirectoryLength = CheckedMultiply(tableCount, 16, "SFNT table directory");
        EnsureRange(data.Length, tableDirectoryOffset, tableDirectoryLength, "SFNT table directory");
        var tables = new Dictionary<uint, OpenTypeTable>(tableCount);
        for (int tableIndex = 0; tableIndex < tableCount; tableIndex++)
        {
            int recordOffset = CheckedAdd(tableDirectoryOffset, CheckedMultiply(tableIndex, 16, "SFNT table record"), "SFNT table record");
            ReadOnlySpan<byte> record = data.Span.Slice(recordOffset, 16);
            uint tag = BinaryPrimitives.ReadUInt32BigEndian(record.Slice(0, 4));
            uint checksum = BinaryPrimitives.ReadUInt32BigEndian(record.Slice(4, 4));
            int tableOffset = CheckedUInt32ToInt(BinaryPrimitives.ReadUInt32BigEndian(record.Slice(8, 4)), "table offset");
            int tableLength = CheckedUInt32ToInt(BinaryPrimitives.ReadUInt32BigEndian(record.Slice(12, 4)), "table length");
            EnsureRange(data.Length, tableOffset, tableLength, $"table {OpenTypeTag.ToString(tag)}");
            var table = new OpenTypeTable(tag, checksum, tableOffset, tableLength, data.Slice(tableOffset, tableLength));
            if (!tables.TryAdd(tag, table))
            {
                throw new InvalidDataException($"The SFNT directory contains duplicate table tag {OpenTypeTag.ToString(tag)}.");
            }
        }

        return new OpenTypeFace(faceIndex, directoryOffset, scalerType, tables);
    }

    private static void EnsureRange(int sourceLength, int offset, int length, string field)
    {
        if (offset < 0 || length < 0 || offset > sourceLength - length)
        {
            throw new InvalidDataException($"The {field} range lies outside the font container.");
        }
    }

    private static int CheckedAdd(int left, int right, string field)
    {
        try
        {
            return checked(left + right);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException($"The {field} range overflows the supported address space.", exception);
        }
    }

    private static int CheckedMultiply(int left, int right, string field)
    {
        try
        {
            return checked(left * right);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException($"The {field} length overflows the supported address space.", exception);
        }
    }

    private static int CheckedUInt32ToInt(uint value, string field)
    {
        if (value > int.MaxValue)
        {
            throw new InvalidDataException($"The {field} exceeds the supported address space.");
        }

        return (int)value;
    }
}
