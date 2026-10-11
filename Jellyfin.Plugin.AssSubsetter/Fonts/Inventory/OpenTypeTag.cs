using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

/// <summary>
/// Converts four-character OpenType table tags to and from their big-endian integer representation.
/// </summary>
public static class OpenTypeTag
{
    /// <summary>Converts exactly four ASCII characters into an OpenType tag.</summary>
    /// <param name="value">The four-character tag.</param>
    /// <returns>The big-endian unsigned tag value.</returns>
    /// <exception cref="ArgumentException">Thrown when the value is not exactly four ASCII characters.</exception>
    public static uint FromString(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length != 4 || !value.All(character => character <= 0x7f))
        {
            throw new ArgumentException("An OpenType tag must contain exactly four ASCII characters.", nameof(value));
        }

        Span<byte> bytes = stackalloc byte[4];
        Encoding.ASCII.GetBytes(value, bytes);
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    /// <summary>Converts an OpenType tag to its four-character ASCII representation.</summary>
    /// <param name="value">The big-endian unsigned tag value.</param>
    /// <returns>The four-character tag.</returns>
    public static string ToString(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return Encoding.ASCII.GetString(bytes);
    }
}
