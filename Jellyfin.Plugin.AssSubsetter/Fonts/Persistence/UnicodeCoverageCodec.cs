using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <summary>Encodes normalized inclusive cmap ranges as eight bytes per range.</summary>
internal static class UnicodeCoverageCodec
{
    public static byte[] Encode(IReadOnlyList<UnicodeRange> ranges)
    {
        byte[] bytes = new byte[checked(ranges.Count * 8)];
        for (int index = 0; index < ranges.Count; index++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(index * 8, 4), ranges[index].Start);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan((index * 8) + 4, 4), ranges[index].End);
        }

        return bytes;
    }

    public static IReadOnlyList<UnicodeRange> Decode(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length % 8 != 0)
        {
            throw new InvalidDataException("Unicode coverage has an invalid encoded length.");
        }

        var ranges = new UnicodeRange[bytes.Length / 8];
        for (int index = 0; index < ranges.Length; index++)
        {
            uint start = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(index * 8, 4));
            uint end = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan((index * 8) + 4, 4));
            if (start > end || end > 0x10FFFF || (index > 0 && start <= ranges[index - 1].End))
            {
                throw new InvalidDataException("Unicode coverage contains an invalid or overlapping range.");
            }

            ranges[index] = new UnicodeRange(start, end);
        }

        return ranges;
    }
}
