using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace Jellyfin.Plugin.AssSubsetter.Services;

/// <summary>
/// Rewrites the OpenType 'name' table in a font binary to replace the font family name
/// with a unique random prefix, enabling correct font matching in ASS subtitle embedding.
/// </summary>
public static class FontNameRewriter
{
    private const uint NameTag = 0x6E616D65; // 'name' in big-endian

    // Name IDs that contain font family/identity information and must be renamed.
    private static readonly ushort[] TargetNameIds = new ushort[] { 1, 4, 6, 16 };

    /// <summary>
    /// Generates a random 8-character uppercase alphanumeric prefix, matching the style used by mkvlib/mkvtool.
    /// </summary>
    /// <returns>An 8-character random string.</returns>
    public static string GenerateRandomPrefix()
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var random = new Random();
        var sb = new StringBuilder(8);
        for (int i = 0; i < 8; i++)
        {
            sb.Append(chars[random.Next(chars.Length)]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Renames the font family in the binary font data by replacing name table entries
    /// (Name IDs 1, 4, 6, 16) with the specified new family name.
    /// </summary>
    /// <param name="fontData">The raw binary font data (TTF/OTF).</param>
    /// <param name="newFamilyName">The new family name to set (e.g., a random 8-char prefix).</param>
    /// <returns>The modified font binary with the renamed family, or null if the operation failed.</returns>
    public static byte[]? RenameFontFamily(byte[] fontData, string newFamilyName)
    {
        if (fontData == null || fontData.Length < 12)
        {
            return null;
        }

        // Parse the offset table (font directory)
        // sfVersion (4 bytes) | numTables (2) | searchRange (2) | entrySelector (2) | rangeShift (2)
        int sfVersion = BinaryPrimitives.ReadInt32BigEndian(fontData.AsSpan(0, 4));

        // Accept TrueType (0x00010000 or 'true') and OpenType ('OTTO')
        bool isTrueType = sfVersion == 0x00010000 || sfVersion == 0x74727565; // 'true'
        bool isOpenType = sfVersion == 0x4F54544F; // 'OTTO'
        if (!isTrueType && !isOpenType)
        {
            return null;
        }

        ushort numTables = BinaryPrimitives.ReadUInt16BigEndian(fontData.AsSpan(4, 2));

        // Find the 'name' table in the table directory
        int nameTableOffset = -1;
        int nameTableLength = -1;
        int nameTableDirEntryOffset = -1;

        for (int i = 0; i < numTables; i++)
        {
            int dirOffset = 12 + (i * 16);
            if (dirOffset + 16 > fontData.Length)
            {
                return null;
            }

            uint tag = BinaryPrimitives.ReadUInt32BigEndian(fontData.AsSpan(dirOffset, 4));
            if (tag == NameTag)
            {
                nameTableDirEntryOffset = dirOffset;
                nameTableOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(fontData.AsSpan(dirOffset + 8, 4));
                nameTableLength = (int)BinaryPrimitives.ReadUInt32BigEndian(fontData.AsSpan(dirOffset + 12, 4));
                break;
            }
        }

        if (nameTableOffset < 0 || nameTableOffset + nameTableLength > fontData.Length)
        {
            return null;
        }

        // Parse the 'name' table
        // format (2) | count (2) | stringOffset (2) | nameRecords[count] (12 each)
        var nameSpan = fontData.AsSpan(nameTableOffset, nameTableLength);
        if (nameSpan.Length < 6)
        {
            return null;
        }

        ushort nameCount = BinaryPrimitives.ReadUInt16BigEndian(nameSpan.Slice(2, 2));
        ushort storageOffset = BinaryPrimitives.ReadUInt16BigEndian(nameSpan.Slice(4, 2));

        // Build new name records and string storage
        var newRecords = new List<NameRecord>();
        var newStringData = new List<byte>();

        for (int i = 0; i < nameCount; i++)
        {
            int recordOffset = 6 + (i * 12);
            if (recordOffset + 12 > nameSpan.Length)
            {
                break;
            }

            ushort platformId = BinaryPrimitives.ReadUInt16BigEndian(nameSpan.Slice(recordOffset, 2));
            ushort encodingId = BinaryPrimitives.ReadUInt16BigEndian(nameSpan.Slice(recordOffset + 2, 2));
            ushort languageId = BinaryPrimitives.ReadUInt16BigEndian(nameSpan.Slice(recordOffset + 4, 2));
            ushort nameId = BinaryPrimitives.ReadUInt16BigEndian(nameSpan.Slice(recordOffset + 6, 2));
            ushort length = BinaryPrimitives.ReadUInt16BigEndian(nameSpan.Slice(recordOffset + 8, 2));
            ushort offset = BinaryPrimitives.ReadUInt16BigEndian(nameSpan.Slice(recordOffset + 10, 2));

            byte[] stringBytes;
            bool isTargetNameId = Array.IndexOf(TargetNameIds, nameId) >= 0;

            if (isTargetNameId)
            {
                // Replace with new family name
                if (nameId == 6)
                {
                    // PostScript name: ASCII, no spaces
                    stringBytes = GetEncodedString(newFamilyName, platformId, encodingId, isPostScript: true);
                }
                else
                {
                    stringBytes = GetEncodedString(newFamilyName, platformId, encodingId, isPostScript: false);
                }
            }
            else
            {
                // Keep original string data
                int srcStart = storageOffset + offset;
                if (srcStart + length <= nameSpan.Length)
                {
                    stringBytes = nameSpan.Slice(srcStart, length).ToArray();
                }
                else
                {
                    stringBytes = Array.Empty<byte>();
                }
            }

            ushort newOffset = (ushort)newStringData.Count;
            newRecords.Add(new NameRecord
            {
                PlatformId = platformId,
                EncodingId = encodingId,
                LanguageId = languageId,
                NameId = nameId,
                Length = (ushort)stringBytes.Length,
                Offset = newOffset,
            });
            newStringData.AddRange(stringBytes);
        }

        // Build the new name table
        int newNameTableSize = 6 + (newRecords.Count * 12) + newStringData.Count;
        var newNameTable = new byte[newNameTableSize];
        var newNameSpan = newNameTable.AsSpan();

        // Header
        BinaryPrimitives.WriteUInt16BigEndian(newNameSpan.Slice(0, 2), 0); // format
        BinaryPrimitives.WriteUInt16BigEndian(newNameSpan.Slice(2, 2), (ushort)newRecords.Count);
        ushort newStorageOffset = (ushort)(6 + (newRecords.Count * 12));
        BinaryPrimitives.WriteUInt16BigEndian(newNameSpan.Slice(4, 2), newStorageOffset);

        // Records
        for (int i = 0; i < newRecords.Count; i++)
        {
            int recOff = 6 + (i * 12);
            var rec = newRecords[i];
            BinaryPrimitives.WriteUInt16BigEndian(newNameSpan.Slice(recOff, 2), rec.PlatformId);
            BinaryPrimitives.WriteUInt16BigEndian(newNameSpan.Slice(recOff + 2, 2), rec.EncodingId);
            BinaryPrimitives.WriteUInt16BigEndian(newNameSpan.Slice(recOff + 4, 2), rec.LanguageId);
            BinaryPrimitives.WriteUInt16BigEndian(newNameSpan.Slice(recOff + 6, 2), rec.NameId);
            BinaryPrimitives.WriteUInt16BigEndian(newNameSpan.Slice(recOff + 8, 2), rec.Length);
            BinaryPrimitives.WriteUInt16BigEndian(newNameSpan.Slice(recOff + 10, 2), rec.Offset);
        }

        // String storage
        newStringData.CopyTo(0, newNameTable, newStorageOffset, newStringData.Count);

        // Now rebuild the entire font with the new name table
        return RebuildFontWithNewNameTable(fontData, nameTableDirEntryOffset, nameTableOffset, nameTableLength, newNameTable, numTables);
    }

    private static byte[] GetEncodedString(string text, ushort platformId, ushort encodingId, bool isPostScript)
    {
        if (isPostScript)
        {
            // PostScript names must be ASCII, no spaces, max 63 chars
            string psName = text.Replace(" ", string.Empty, StringComparison.Ordinal);
            if (platformId == 3 && (encodingId == 1 || encodingId == 10))
            {
                return Encoding.BigEndianUnicode.GetBytes(psName);
            }

            return Encoding.ASCII.GetBytes(psName);
        }

        if (platformId == 3 && (encodingId == 1 || encodingId == 10))
        {
            // Windows Unicode BMP or full repertoire
            return Encoding.BigEndianUnicode.GetBytes(text);
        }

        if (platformId == 1 && encodingId == 0)
        {
            // Macintosh Roman
            return Encoding.UTF8.GetBytes(text);
        }

        // Fallback: try BigEndianUnicode for unknown platform/encoding combinations
        return Encoding.BigEndianUnicode.GetBytes(text);
    }

    private static byte[]? RebuildFontWithNewNameTable(
        byte[] originalFont,
        int nameTableDirEntryOffset,
        int oldNameTableOffset,
        int oldNameTableLength,
        byte[] newNameTable,
        ushort numTables)
    {
        // Pad old and new name tables to 4-byte boundaries
        int oldPaddedLen = (oldNameTableLength + 3) & ~3;
        int newPaddedLen = (newNameTable.Length + 3) & ~3;
        int sizeDelta = newPaddedLen - oldPaddedLen;

        // Build the new font
        byte[] newFont = new byte[originalFont.Length + sizeDelta];

        // Copy everything before the name table
        Array.Copy(originalFont, 0, newFont, 0, oldNameTableOffset);

        // Copy new name table (with padding)
        Array.Copy(newNameTable, 0, newFont, oldNameTableOffset, newNameTable.Length);
        // Zero-fill padding
        for (int i = newNameTable.Length; i < newPaddedLen; i++)
        {
            newFont[oldNameTableOffset + i] = 0;
        }

        // Copy everything after the old name table
        int afterOldNameTable = oldNameTableOffset + oldPaddedLen;
        if (afterOldNameTable < originalFont.Length)
        {
            Array.Copy(originalFont, afterOldNameTable, newFont, oldNameTableOffset + newPaddedLen, originalFont.Length - afterOldNameTable);
        }

        // Update the table directory entry for 'name': offset stays the same, update length
        BinaryPrimitives.WriteUInt32BigEndian(newFont.AsSpan(nameTableDirEntryOffset + 12, 4), (uint)newNameTable.Length);

        // Update the checksum for the name table entry
        uint newChecksum = CalcTableChecksum(newFont, oldNameTableOffset, newPaddedLen);
        BinaryPrimitives.WriteUInt32BigEndian(newFont.AsSpan(nameTableDirEntryOffset + 4, 4), newChecksum);

        // Update offsets for all tables that come after the name table
        for (int i = 0; i < numTables; i++)
        {
            int dirOffset = 12 + (i * 16);
            uint tableOffset = BinaryPrimitives.ReadUInt32BigEndian(newFont.AsSpan(dirOffset + 8, 4));
            if (tableOffset > (uint)oldNameTableOffset)
            {
                uint adjusted = (uint)((int)tableOffset + sizeDelta);
                BinaryPrimitives.WriteUInt32BigEndian(newFont.AsSpan(dirOffset + 8, 4), adjusted);
            }
        }

        // Recalculate checksums for tables whose offset changed
        if (sizeDelta != 0)
        {
            for (int i = 0; i < numTables; i++)
            {
                int dirOffset = 12 + (i * 16);
                uint tag = BinaryPrimitives.ReadUInt32BigEndian(newFont.AsSpan(dirOffset, 4));
                if (tag == NameTag)
                {
                    continue; // Already updated
                }

                uint tblOffset = BinaryPrimitives.ReadUInt32BigEndian(newFont.AsSpan(dirOffset + 8, 4));
                uint tblLength = BinaryPrimitives.ReadUInt32BigEndian(newFont.AsSpan(dirOffset + 12, 4));

                if ((int)tblOffset > oldNameTableOffset && tblOffset + tblLength <= (uint)newFont.Length)
                {
                    uint chk = CalcTableChecksum(newFont, (int)tblOffset, (int)((tblLength + 3) & ~3));
                    BinaryPrimitives.WriteUInt32BigEndian(newFont.AsSpan(dirOffset + 4, 4), chk);
                }
            }
        }

        return newFont;
    }

    private static uint CalcTableChecksum(byte[] data, int offset, int length)
    {
        uint sum = 0;
        int end = Math.Min(offset + length, data.Length);
        for (int i = offset; i + 3 < end; i += 4)
        {
            sum += BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i, 4));
        }

        // Handle remaining bytes (if length not multiple of 4)
        int remaining = end - (((end - offset) / 4 * 4) + offset);
        if (remaining > 0)
        {
            int lastStart = end - remaining;
            uint val = 0;
            for (int i = 0; i < remaining; i++)
            {
                val |= (uint)data[lastStart + i] << (24 - (i * 8));
            }

            sum += val;
        }

        return sum;
    }

    private struct NameRecord
    {
        public ushort PlatformId;
        public ushort EncodingId;
        public ushort LanguageId;
        public ushort NameId;
        public ushort Length;
        public ushort Offset;
    }
}
