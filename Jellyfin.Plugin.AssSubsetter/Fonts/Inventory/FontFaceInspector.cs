using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SkiaSharp;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

/// <summary>
/// Extracts deterministic OpenType name, style, coverage, vertical, color, and variation evidence.
/// </summary>
public static class FontFaceInspector
{
    private static readonly uint NameTag = OpenTypeTag.FromString("name");
    private static readonly uint CmapTag = OpenTypeTag.FromString("cmap");
    private static readonly uint HeadTag = OpenTypeTag.FromString("head");
    private static readonly uint Os2Tag = OpenTypeTag.FromString("OS/2");
    private static readonly uint PostTag = OpenTypeTag.FromString("post");
    private static readonly uint FvarTag = OpenTypeTag.FromString("fvar");

    /// <summary>Extracts deterministic evidence from a validated Face without opening the source file again.</summary>
    /// <param name="face">The bounds-checked Face table directory.</param>
    /// <returns>A classifiable result, or a refusal result with the failed proof.</returns>
    public static InspectedFontFace Inspect(OpenTypeFace face)
    {
        ArgumentNullException.ThrowIfNull(face);
        try
        {
            List<OpenTypeNameRecord> nameRecords = ReadNames(GetRequiredTable(face, NameTag));
            string familyName = SelectName(nameRecords, 16) ?? SelectName(nameRecords, 1) ?? SelectName(nameRecords, 4) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(familyName))
            {
                return InspectedFontFace.Unclassifiable(face.FaceIndex, "No usable family identity exists in the name table.");
            }

            string styleName = SelectName(nameRecords, 17) ?? SelectName(nameRecords, 2) ?? "Regular";
            (List<UnicodeRange> coverage, uint[] selectors, List<UnicodeVariationMapping> mappings, Dictionary<uint, ushort> ordinary, bool complete, int validCmaps, string? coverageError) = ReadCoverage(GetRequiredTable(face, CmapTag));
            if (coverage.Count == 0)
            {
                return InspectedFontFace.Unclassifiable(face.FaceIndex, coverageError ?? "No valid encoded Unicode coverage could be proven from cmap.");
            }

            (int weight, int width, bool italic) = ReadStyle(face);
            int unitsPerEm = ReadUnitsPerEm(face);
            List<FontVariationAxis> axes = ReadVariationAxes(face);
            string? headRevision = ReadHeadVersion(face);
            string? version = SelectName(nameRecords, 5) ?? headRevision;
            string[] aliases = nameRecords
                .Where(record => record.NameId is 1 or 4 or 6 or 16)
                .Select(record => record.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return new InspectedFontFace
            {
                FaceIndex = face.FaceIndex,
                IsClassifiable = true,
                FamilyName = familyName,
                StyleName = styleName,
                Names = aliases,
                NameRecords = nameRecords,
                Weight = weight,
                Width = width,
                Slant = italic ? 1 : 0,
                IsItalic = italic,
                IsFixedPitch = ReadFixedPitch(face),
                Technology = GetTechnology(face),
                UnitsPerEm = unitsPerEm,
                Version = version,
                HeadRevision = headRevision,
                VendorId = ReadVendorId(face),
                ValidCmapCount = validCmaps,
                UnicodeCoverage = coverage,
                UnicodeMappings = ordinary,
                IsMappingComplete = complete,
                VariationSelectors = selectors,
                VariationMappings = mappings,
                VariationAxes = axes,
                HasVerticalMetrics = HasTables(face, "vhea", "vmtx"),
                HasLayoutTables = HasAnyTable(face, "GDEF", "GPOS", "GSUB", "BASE", "JSTF", "MATH", "kern", "kerx", "morx"),
                HasColorOrBitmapGlyphs = HasAnyTable(face, "COLR", "CPAL", "CBDT", "CBLC", "sbix", "SVG ", "EBDT", "EBLC")
            };
        }
        catch (InvalidDataException exception)
        {
            return InspectedFontFace.Unclassifiable(face.FaceIndex, exception.Message);
        }
    }

    /// <summary>Extracts deterministic evidence and independently verifies that Skia can open the same physical Face.</summary>
    /// <param name="path">The physical font file path.</param>
    /// <param name="face">The bounds-checked Face table directory.</param>
    /// <returns>A classifiable result only when both direct parsing and Skia validation succeed.</returns>
    public static InspectedFontFace Inspect(string path, OpenTypeFace face)
    {
        InspectedFontFace inspected = Inspect(face);
        if (!inspected.IsClassifiable)
        {
            return inspected;
        }

        using SKTypeface? typeface = SKTypeface.FromFile(path, face.FaceIndex);
        return typeface is null
            ? InspectedFontFace.Unclassifiable(face.FaceIndex, "Skia could not open the parsed physical Face.")
            : inspected;
    }

    private static OpenTypeTable GetRequiredTable(OpenTypeFace face, uint tag)
    {
        if (!face.Tables.TryGetValue(tag, out OpenTypeTable? table))
        {
            throw new InvalidDataException($"Required table {OpenTypeTag.ToString(tag)} is missing.");
        }

        return table;
    }

    private static List<OpenTypeNameRecord> ReadNames(OpenTypeTable table)
    {
        ReadOnlySpan<byte> data = table.Data.Span;
        EnsureRange(data.Length, 0, 6, "name header");
        ushort format = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(0, 2));
        if (format is not 0 and not 1)
        {
            throw new InvalidDataException("The name-table format is unsupported.");
        }

        ushort count = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2, 2));
        ushort stringOffset = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(4, 2));
        EnsureRange(data.Length, 6, checked(count * 12), "name records");
        if (stringOffset > data.Length)
        {
            throw new InvalidDataException("The name string storage offset lies outside the table.");
        }

        var records = new List<OpenTypeNameRecord>(count);
        for (int index = 0; index < count; index++)
        {
            int offset = 6 + (index * 12);
            ushort platformId = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
            ushort encodingId = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 2, 2));
            ushort languageId = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 4, 2));
            ushort nameId = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 6, 2));
            ushort length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 8, 2));
            ushort relativeOffset = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 10, 2));
            int valueOffset = checked(stringOffset + relativeOffset);
            EnsureRange(data.Length, valueOffset, length, "name value");
            string? value = DecodeName(platformId, data.Slice(valueOffset, length));
            if (!string.IsNullOrWhiteSpace(value) && value.All(character => !char.IsControl(character)))
            {
                records.Add(new OpenTypeNameRecord(platformId, encodingId, languageId, nameId, value.Trim()));
            }
        }

        return records;
    }

    private static string? DecodeName(ushort platformId, ReadOnlySpan<byte> data)
    {
        if (platformId is 0 or 3)
        {
            return data.Length % 2 == 0 ? Encoding.BigEndianUnicode.GetString(data) : null;
        }

        return platformId == 1 ? Encoding.Latin1.GetString(data) : null;
    }

    private static string? SelectName(IReadOnlyList<OpenTypeNameRecord> records, ushort nameId)
    {
        return OpenTypeNameSelector.SelectPreferred(records.Where(record => record.NameId == nameId));
    }

    private static (List<UnicodeRange> Coverage, uint[] Selectors, List<UnicodeVariationMapping> Mappings, Dictionary<uint, ushort> Ordinary, bool Complete, int ValidCount, string? Failure) ReadCoverage(OpenTypeTable table)
    {
        ReadOnlySpan<byte> data = table.Data.Span;
        EnsureRange(data.Length, 0, 4, "cmap header");
        ushort count = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2, 2));
        EnsureRange(data.Length, 4, checked(count * 8), "cmap encoding records");
        var ranges = new List<UnicodeRange>();
        var selectors = new SortedSet<uint>();
        var mappings = new List<UnicodeVariationMapping>();
        var ordinary = new Dictionary<uint, ushort>();
        bool complete = true;
        int validCount = 0;
        bool sawUnicodeRecord = false;
        string? failure = null;
        for (int index = 0; index < count; index++)
        {
            int recordOffset = 4 + (index * 8);
            ushort platform = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(recordOffset, 2));
            ushort encoding = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(recordOffset + 2, 2));
            uint rawOffset = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(recordOffset + 4, 4));
            bool unicodeRecord = platform == 0 || (platform == 3 && encoding is 1 or 10);
            sawUnicodeRecord |= unicodeRecord;
            if (rawOffset > int.MaxValue || rawOffset > data.Length - 2)
            {
                if (platform == 0 && encoding == 5)
                {
                    throw new InvalidDataException("The Unicode variation subtable offset is outside cmap.");
                }

                complete = false;
                if (unicodeRecord)
                {
                    failure ??= "A Unicode cmap record points outside the table.";
                }

                continue;
            }

            if (!unicodeRecord)
            {
                // A valid legacy mapping is separate from the Unicode evidence.
                continue;
            }

            int subtableOffset = (int)rawOffset;
            ushort format = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(subtableOffset, 2));
            var subtableRanges = new List<UnicodeRange>();
            var subtableMappings = new Dictionary<uint, ushort>();
            try
            {
                switch (format)
                {
                    case 4:
                        ReadFormat4(data.Slice(subtableOffset), subtableRanges, subtableMappings);
                        validCount++;
                        break;
                    case 12:
                        ReadFormat12(data.Slice(subtableOffset), subtableRanges, subtableMappings);
                        validCount++;
                        break;
                    case 14:
                        if (platform != 0 || encoding != 5)
                        {
                            throw new InvalidDataException("The variation subtable has an invalid encoding record.");
                        }

                        ReadFormat14(data.Slice(subtableOffset), selectors, mappings);
                        validCount++;
                        break;
                    default:
                        complete = false;
                        failure ??= $"Unicode cmap format {format} is unsupported.";
                        break;
                }
            }
            catch (InvalidDataException exception) when (format != 14)
            {
                // A malformed cmap subtable is excluded while independent valid subtables remain usable evidence.
                complete = false;
                failure ??= exception.Message;
                continue;
            }

            ranges.AddRange(subtableRanges);
            foreach (KeyValuePair<uint, ushort> pair in subtableMappings)
            {
                AddMapping(ordinary, pair.Key, pair.Value);
            }
        }

        List<UnicodeRange> coverage = MergeRanges(ranges);
        if (coverage.Count == 0)
        {
            failure ??= sawUnicodeRecord
                ? "Unicode cmap has no valid nonzero glyph mappings."
                : "Only legacy cmap encoding records are present; no Unicode mapping can be proven.";
        }

        return (coverage, selectors.ToArray(), mappings, ordinary, complete, validCount, failure);
    }

    private static void ReadFormat12(ReadOnlySpan<byte> data, List<UnicodeRange> ranges, Dictionary<uint, ushort> mappings)
    {
        EnsureRange(data.Length, 0, 16, "cmap format 12 header");
        uint rawLength = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4));
        int length = CheckedUInt32ToInt(rawLength, "cmap format 12 length");
        EnsureRange(data.Length, 0, length, "cmap format 12");
        uint groupCount = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(12, 4));
        if (groupCount > 0x20000)
        {
            throw new InvalidDataException("The cmap format 12 group count is unreasonable.");
        }

        int groupBytes = checked((int)groupCount * 12);
        EnsureRange(length, 16, groupBytes, "cmap format 12 groups");
        for (int index = 0; index < groupCount; index++)
        {
            int offset = 16 + (index * 12);
            uint start = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
            uint end = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 4, 4));
            uint startGlyph = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 8, 4));
            if (start > end || end > 0x10ffff || (ulong)startGlyph + end - start > ushort.MaxValue)
            {
                throw new InvalidDataException("A cmap format 12 group has an invalid scalar range.");
            }

            if (startGlyph == 0)
            {
                if (start == end)
                {
                    continue;
                }

                start++;
                startGlyph++;
            }

            ranges.Add(new UnicodeRange(start, end));
            for (uint scalar = start; scalar <= end; scalar++)
            {
                AddMapping(mappings, scalar, checked((ushort)(startGlyph + scalar - start)));
            }
        }
    }

    private static void ReadFormat4(ReadOnlySpan<byte> data, List<UnicodeRange> ranges, Dictionary<uint, ushort> mappings)
    {
        EnsureRange(data.Length, 0, 16, "cmap format 4 header");
        int length = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2, 2));
        EnsureRange(data.Length, 0, length, "cmap format 4");
        int segmentCount = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(6, 2)) / 2;
        if (segmentCount is <= 0 or > 0x2000)
        {
            throw new InvalidDataException("The cmap format 4 segment count is invalid.");
        }

        int endCodes = 14;
        int startCodes = checked(endCodes + (segmentCount * 2) + 2);
        int deltas = checked(startCodes + (segmentCount * 2));
        int rangeOffsets = checked(deltas + (segmentCount * 2));
        EnsureRange(length, rangeOffsets, segmentCount * 2, "cmap format 4 arrays");
        var covered = new bool[0x10000];
        for (int segment = 0; segment < segmentCount; segment++)
        {
            ushort end = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(endCodes + (segment * 2), 2));
            ushort start = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(startCodes + (segment * 2), 2));
            short delta = BinaryPrimitives.ReadInt16BigEndian(data.Slice(deltas + (segment * 2), 2));
            ushort rangeOffset = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(rangeOffsets + (segment * 2), 2));
            if (start > end)
            {
                throw new InvalidDataException("A cmap format 4 segment starts after it ends.");
            }

            for (int codePoint = start; codePoint <= end && codePoint != 0xffff; codePoint++)
            {
                int glyph;
                if (rangeOffset == 0)
                {
                    glyph = (codePoint + delta) & 0xffff;
                }
                else
                {
                    int rangeWord = rangeOffsets + (segment * 2);
                    int glyphOffset = checked(rangeWord + rangeOffset + ((codePoint - start) * 2));
                    if (glyphOffset > length - 2)
                    {
                        throw new InvalidDataException("The cmap format 4 glyph array reference lies outside the declared subtable length.");
                    }

                    glyph = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(glyphOffset, 2));
                    if (glyph != 0)
                    {
                        glyph = (glyph + delta) & 0xffff;
                    }
                }

                covered[codePoint] = glyph != 0;
                if (glyph != 0)
                {
                    AddMapping(mappings, (uint)codePoint, checked((ushort)glyph));
                }
            }
        }

        AddBooleanRanges(covered, ranges);
    }

    private static void AddMapping(Dictionary<uint, ushort> mappings, uint scalar, ushort glyph)
    {
        if (scalar is >= 0xd800 and <= 0xdfff || (mappings.TryGetValue(scalar, out ushort previous) && previous != glyph))
        {
            throw new InvalidDataException("Unicode cmap mappings are invalid or inconsistent across subtables.");
        }

        mappings[scalar] = glyph;
    }

    private static void ReadFormat14(ReadOnlySpan<byte> data, SortedSet<uint> selectors, List<UnicodeVariationMapping> mappings)
    {
        EnsureRange(data.Length, 0, 10, "cmap format 14 header");
        int length = CheckedUInt32ToInt(BinaryPrimitives.ReadUInt32BigEndian(data.Slice(2, 4)), "cmap format 14 length");
        EnsureRange(data.Length, 0, length, "cmap format 14");
        uint recordCount = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(6, 4));
        if (recordCount > 0x1000)
        {
            throw new InvalidDataException("The cmap format 14 selector count is unreasonable.");
        }

        EnsureRange(length, 10, checked((int)recordCount * 11), "cmap format 14 selector records");
        for (int index = 0; index < recordCount; index++)
        {
            int offset = 10 + (index * 11);
            uint selector = ReadUInt24(data, offset);
            if (selector is not (>= 0xfe00 and <= 0xfe0f) and not (>= 0xe0100 and <= 0xe01ef)
                || !selectors.Add(selector))
            {
                throw new InvalidDataException("A cmap format 14 selector is invalid or duplicated.");
            }

            int defaultOffset = CheckedUInt32ToInt(BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 3, 4)), "default UVS offset");
            int explicitOffset = CheckedUInt32ToInt(BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset + 7, 4)), "non-default UVS offset");
            var baseScalars = new HashSet<uint>();
            ReadVariationMappings(data.Slice(0, length), defaultOffset, selector, true, baseScalars, mappings);
            ReadVariationMappings(data.Slice(0, length), explicitOffset, selector, false, baseScalars, mappings);
        }
    }

    private static void ReadVariationMappings(ReadOnlySpan<byte> data, int offset, uint selector, bool isDefault, HashSet<uint> scalars, List<UnicodeVariationMapping> mappings)
    {
        if (offset == 0)
        {
            return;
        }

        EnsureRange(data.Length, offset, 4, "UVS mapping count");
        int count = CheckedUInt32ToInt(BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4)), "UVS mapping count");
        int recordSize = isDefault ? 4 : 5;
        if (count > 0x110000 / recordSize || count > (data.Length - offset - 4) / recordSize)
        {
            throw new InvalidDataException("A cmap format 14 mapping array is outside its subtable.");
        }

        for (int index = 0; index < count; index++)
        {
            int recordOffset = offset + 4 + (index * recordSize);
            uint start = ReadUInt24(data, recordOffset);
            uint end = start + (isDefault ? data[recordOffset + 3] : 0U);
            ushort? glyph = isDefault ? null : BinaryPrimitives.ReadUInt16BigEndian(data.Slice(recordOffset + 3, 2));
            for (uint scalar = start; scalar <= end; scalar++)
            {
                if (scalar > 0x10ffff || scalar is >= 0xd800 and <= 0xdfff || glyph == 0 || !scalars.Add(scalar) || mappings.Count >= 0x110000)
                {
                    throw new InvalidDataException("A cmap format 14 mapping is invalid, overlapping, or unreasonable.");
                }

                mappings.Add(new UnicodeVariationMapping(scalar, selector, glyph));
            }
        }
    }

    private static uint ReadUInt24(ReadOnlySpan<byte> data, int offset)
        => ((uint)data[offset] << 16) | ((uint)data[offset + 1] << 8) | data[offset + 2];

    private static void AddBooleanRanges(bool[] covered, List<UnicodeRange> ranges)
    {
        int index = 0;
        while (index < covered.Length)
        {
            if (!covered[index])
            {
                index++;
                continue;
            }

            int start = index;
            while (index + 1 < covered.Length && covered[index + 1])
            {
                index++;
            }

            ranges.Add(new UnicodeRange((uint)start, (uint)index));
            index++;
        }
    }

    private static List<UnicodeRange> MergeRanges(IEnumerable<UnicodeRange> ranges)
    {
        var result = new List<UnicodeRange>();
        foreach (UnicodeRange range in ranges.OrderBy(value => value.Start).ThenBy(value => value.End))
        {
            if (result.Count == 0 || range.Start > result[^1].End + 1)
            {
                result.Add(range);
                continue;
            }

            UnicodeRange previous = result[^1];
            result[^1] = previous with { End = Math.Max(previous.End, range.End) };
        }

        return result;
    }

    private static (int Weight, int Width, bool Italic) ReadStyle(OpenTypeFace face)
    {
        if (!face.Tables.TryGetValue(Os2Tag, out OpenTypeTable? table))
        {
            return (400, 5, false);
        }

        ReadOnlySpan<byte> data = table.Data.Span;
        EnsureRange(data.Length, 4, 4, "OS/2 weight and width");
        int weight = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(4, 2));
        int width = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(6, 2));
        bool italic = data.Length >= 64 && (BinaryPrimitives.ReadUInt16BigEndian(data.Slice(62, 2)) & 1) != 0;
        return (weight, width, italic);
    }

    private static bool ReadFixedPitch(OpenTypeFace face)
    {
        return face.Tables.TryGetValue(PostTag, out OpenTypeTable? table)
            && table.Data.Length >= 16
            && BinaryPrimitives.ReadUInt32BigEndian(table.Data.Span.Slice(12, 4)) != 0;
    }

    private static int ReadUnitsPerEm(OpenTypeFace face)
    {
        if (!face.Tables.TryGetValue(HeadTag, out OpenTypeTable? table) || table.Data.Length < 20)
        {
            return 0;
        }

        return BinaryPrimitives.ReadUInt16BigEndian(table.Data.Span.Slice(18, 2));
    }

    private static string? ReadHeadVersion(OpenTypeFace face)
    {
        if (!face.Tables.TryGetValue(HeadTag, out OpenTypeTable? table) || table.Data.Length < 8)
        {
            return null;
        }

        int raw = BinaryPrimitives.ReadInt32BigEndian(table.Data.Span.Slice(4, 4));
        return (raw / 65536f).ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static string? ReadVendorId(OpenTypeFace face)
    {
        if (!face.Tables.TryGetValue(Os2Tag, out OpenTypeTable? table) || table.Data.Length < 62)
        {
            return null;
        }

        string vendorId = Encoding.ASCII.GetString(table.Data.Span.Slice(58, 4)).Trim();
        return vendorId.Length > 0 && vendorId.All(character => !char.IsControl(character))
            ? vendorId
            : null;
    }

    private static List<FontVariationAxis> ReadVariationAxes(OpenTypeFace face)
    {
        if (!face.Tables.TryGetValue(FvarTag, out OpenTypeTable? table))
        {
            return [];
        }

        ReadOnlySpan<byte> data = table.Data.Span;
        EnsureRange(data.Length, 0, 16, "fvar header");
        int axesOffset = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(4, 2));
        int axisCount = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(8, 2));
        int axisSize = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(10, 2));
        if (axisCount > 64 || axisSize < 20)
        {
            throw new InvalidDataException("The fvar axis layout is invalid.");
        }

        EnsureRange(data.Length, axesOffset, checked(axisCount * axisSize), "fvar axes");
        var axes = new List<FontVariationAxis>(axisCount);
        for (int index = 0; index < axisCount; index++)
        {
            int offset = axesOffset + (index * axisSize);
            uint tag = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
            float minimum = ReadFixed(data.Slice(offset + 4, 4));
            float defaultValue = ReadFixed(data.Slice(offset + 8, 4));
            float maximum = ReadFixed(data.Slice(offset + 12, 4));
            if (minimum > defaultValue || defaultValue > maximum)
            {
                throw new InvalidDataException("An fvar axis default lies outside its bounds.");
            }

            axes.Add(new FontVariationAxis(
                tag,
                minimum,
                defaultValue,
                maximum,
                BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 16, 2)),
                BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 18, 2))));
        }

        return axes;
    }

    private static string GetTechnology(OpenTypeFace face)
    {
        if (HasAnyTable(face, "CFF2"))
        {
            return "OpenType CFF2";
        }

        if (HasAnyTable(face, "CFF "))
        {
            return "OpenType CFF";
        }

        if (HasTables(face, "glyf", "loca"))
        {
            return "TrueType";
        }

        return "OpenType";
    }

    private static bool HasTables(OpenTypeFace face, params string[] tags) => tags.All(tag => face.Tables.ContainsKey(OpenTypeTag.FromString(tag)));

    private static bool HasAnyTable(OpenTypeFace face, params string[] tags) => tags.Any(tag => face.Tables.ContainsKey(OpenTypeTag.FromString(tag)));

    private static float ReadFixed(ReadOnlySpan<byte> data) => BinaryPrimitives.ReadInt32BigEndian(data) / 65536f;

    private static int CheckedUInt32ToInt(uint value, string field)
    {
        if (value > int.MaxValue)
        {
            throw new InvalidDataException($"The {field} exceeds the supported address space.");
        }

        return (int)value;
    }

    private static void EnsureRange(int sourceLength, int offset, int length, string field)
    {
        if (offset < 0 || length < 0 || offset > sourceLength - length)
        {
            throw new InvalidDataException($"The {field} range lies outside its OpenType table.");
        }
    }
}
