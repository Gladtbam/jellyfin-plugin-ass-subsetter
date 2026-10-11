using System;
using System.Buffers.Binary;
using System.IO;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

/// <summary>Removes validated TrueType instruction fields and storage padding from outline evidence.</summary>
internal static class FontGlyphCanonicalizer
{
    internal static bool TryBuild(OpenTypeFace face, out byte[] outlines, out byte[] instructions)
        => TryBuild(face, out outlines, out instructions, out _);

    internal static bool TryBuild(OpenTypeFace face, out byte[] outlines, out byte[] instructions, out string? error)
    {
        outlines = [];
        instructions = [];
        error = null;
        if (!face.Tables.TryGetValue(OpenTypeTag.FromString("glyf"), out OpenTypeTable? glyf)
            || !face.Tables.TryGetValue(OpenTypeTag.FromString("loca"), out OpenTypeTable? loca)
            || !face.Tables.TryGetValue(OpenTypeTag.FromString("head"), out OpenTypeTable? head)
            || !face.Tables.TryGetValue(OpenTypeTag.FromString("maxp"), out OpenTypeTable? maxp)
            || head.Length < 54 || maxp.Length < 6)
        {
            error = "Required TrueType outline tables are missing or truncated.";
            return false;
        }

        try
        {
            int count = Read16(maxp.Data.Span, 4);
            int format = BinaryPrimitives.ReadInt16BigEndian(head.Data.Span.Slice(50, 2));
            int entrySize = format switch { 0 => 2, 1 => 4, _ => throw new InvalidDataException("Unsupported loca format.") };
            if (loca.Length != (count + 1) * entrySize)
            {
                error = "Glyph location table length does not match the declared glyph count.";
                return false;
            }

            using var outlineStream = new MemoryStream();
            using var hintStream = new MemoryStream();
            using var outlineWriter = new BinaryWriter(outlineStream);
            using var hintWriter = new BinaryWriter(hintStream);
            for (int index = 0; index < count; index++)
            {
                int start = Offset(loca.Data.Span, index, entrySize);
                int end = Offset(loca.Data.Span, index + 1, entrySize);
                if (start > end || end > glyf.Length)
                {
                    error = $"Glyph {index} has an invalid outline offset.";
                    return false;
                }

                try
                {
                    CanonicalGlyph(glyf.Data.Span.Slice(start, end - start), count, out byte[] outline, out byte[] hint);
                    outlineWriter.Write(outline.Length);
                    outlineWriter.Write(outline);
                    hintWriter.Write(hint.Length);
                    hintWriter.Write(hint);
                }
                catch (InvalidDataException exception)
                {
                    error = $"Glyph {index}: {exception.Message}";
                    return false;
                }
            }

            outlines = outlineStream.ToArray();
            instructions = hintStream.ToArray();
            return true;
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentOutOfRangeException or OverflowException)
        {
            error = exception.Message;
            return false;
        }
    }

    private static void CanonicalGlyph(ReadOnlySpan<byte> data, int glyphCount, out byte[] outline, out byte[] hint)
    {
        outline = [];
        hint = [];
        if (data.IsEmpty)
        {
            return;
        }

        Require(data, 0, 10);
        short contours = BinaryPrimitives.ReadInt16BigEndian(data);
        int cursor = 10;
        int hintOffset;
        int hintLength;
        if (contours >= 0)
        {
            int points = 0;
            for (int index = 0; index < contours; index++)
            {
                int endpoint = Read16(data, cursor);
                if (endpoint < points)
                {
                    throw new InvalidDataException("Unordered contour endpoints.");
                }

                points = endpoint + 1;
                cursor += 2;
            }

            hintOffset = cursor;
            hintLength = data.Length == cursor && contours == 0 ? 0 : Read16(data, cursor);
            int hintFieldLength = data.Length == cursor && contours == 0 ? 0 : 2;
            cursor += hintFieldLength;
            Require(data, cursor, hintLength);
            hint = data.Slice(cursor, hintLength).ToArray();
            cursor += hintLength;
            int coordinateBytes = 0;
            for (int point = 0; point < points;)
            {
                Require(data, cursor, 1);
                byte flags = data[cursor++];
                int repeat = 1;
                if ((flags & 8) != 0)
                {
                    Require(data, cursor, 1);
                    repeat += data[cursor++];
                }

                if (point + repeat > points || (flags & 0x80) != 0)
                {
                    throw new InvalidDataException("Invalid simple glyph flags.");
                }

                coordinateBytes += repeat * (((flags & 2) != 0 ? 1 : (flags & 16) != 0 ? 0 : 2) + ((flags & 4) != 0 ? 1 : (flags & 32) != 0 ? 0 : 2));
                point += repeat;
            }

            Require(data, cursor, coordinateBytes);
            cursor += coordinateBytes;
            outline = new byte[cursor - hintFieldLength - hintLength];
            data.Slice(0, hintOffset).CopyTo(outline);
            data.Slice(hintOffset + hintFieldLength + hintLength, cursor - hintOffset - hintFieldLength - hintLength).CopyTo(outline.AsSpan(hintOffset));
        }
        else
        {
            if (contours != -1)
            {
                throw new InvalidDataException("Unsupported composite contour count.");
            }

            ushort flags;
            bool hasInstructions = false;
            do
            {
                flags = Read16(data, cursor);
                ushort reference = Read16(data, cursor + 2);
                int scaleFlags = flags & 0xc8;
                // Legacy fonts set reserved bit 0x0010. Preserve it in the outline digest; it does not change field lengths.
                if (reference >= glyphCount || (flags & 0xe000) != 0 || scaleFlags is not (0 or 8 or 0x40 or 0x80))
                {
                    throw new InvalidDataException($"Invalid composite glyph component: flags=0x{flags:X4}, reference={reference}, glyphCount={glyphCount}.");
                }

                hasInstructions |= (flags & 0x100) != 0;
                cursor += 4 + ((flags & 1) != 0 ? 4 : 2);
                cursor += (flags & 8) != 0 ? 2 : (flags & 0x40) != 0 ? 4 : (flags & 0x80) != 0 ? 8 : 0;
                Require(data, 0, cursor);
            }
            while ((flags & 0x20) != 0);

            outline = data.Slice(0, cursor).ToArray();
            int component = 10;
            do
            {
                flags = Read16(outline, component);
                BinaryPrimitives.WriteUInt16BigEndian(outline.AsSpan(component, 2), (ushort)(flags & ~0x100));
                component += 4 + ((flags & 1) != 0 ? 4 : 2);
                component += (flags & 8) != 0 ? 2 : (flags & 0x40) != 0 ? 4 : (flags & 0x80) != 0 ? 8 : 0;
            }
            while ((flags & 0x20) != 0);

            if (hasInstructions)
            {
                hintLength = Read16(data, cursor);
                cursor += 2;
                Require(data, cursor, hintLength);
                hint = data.Slice(cursor, hintLength).ToArray();
                cursor += hintLength;
            }
        }

        if (data.Slice(cursor).IndexOfAnyExcept((byte)0) >= 0)
        {
            throw new InvalidDataException("Nonzero unparsed glyph bytes.");
        }
    }

    private static int Offset(ReadOnlySpan<byte> data, int index, int size)
        => size == 2 ? Read16(data, index * size) * 2 : checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.Slice(index * size, 4)));

    private static ushort Read16(ReadOnlySpan<byte> data, int offset)
    {
        Require(data, offset, 2);
        return BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
    }

    private static void Require(ReadOnlySpan<byte> data, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > data.Length - length)
        {
            throw new InvalidDataException("Truncated glyph field.");
        }
    }
}
