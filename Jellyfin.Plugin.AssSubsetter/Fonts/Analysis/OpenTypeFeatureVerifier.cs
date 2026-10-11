using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;

/// <summary>Checks supported optional OpenType table structures without crediting unsupported formats.</summary>
internal static class OpenTypeFeatureVerifier
{
    private static readonly string[] LayoutTags = ["GDEF", "GPOS", "GSUB", "BASE", "JSTF", "MATH", "kern", "kerx", "morx"];
    private static readonly string[] ColorTags = ["COLR", "CPAL", "CBDT", "CBLC", "sbix", "SVG ", "EBDT", "EBLC"];
    private static readonly string[] VariationTags = ["fvar", "gvar", "CFF2", "avar", "HVAR", "VVAR", "MVAR", "STAT", "cvar"];

    internal static FontVerificationStatus VerifyLayout(OpenTypeFace face)
    {
        string[] present = LayoutTags.Where(tag => Has(face, tag)).ToArray();
        if (present.Length == 0)
        {
            return FontVerificationStatus.NotApplicable;
        }

        FontVerificationStatus result = FontVerificationStatus.Passed;
        int glyphCount = GlyphCount(face);
        foreach (string tag in present)
        {
            if (tag is not "GSUB" and not "GPOS")
            {
                result = FontVerificationStatus.Unknown;
                continue;
            }

            FontVerificationStatus tableResult = ValidateLayoutTable(Get(face, tag).Data.Span, tag == "GSUB", glyphCount);
            if (tableResult == FontVerificationStatus.Failed)
            {
                return FontVerificationStatus.Failed;
            }

            if (tableResult == FontVerificationStatus.Unknown)
            {
                result = FontVerificationStatus.Unknown;
            }
        }

        return result;
    }

    internal static FontVerificationStatus VerifyColor(OpenTypeFace face)
    {
        string[] present = ColorTags.Where(tag => Has(face, tag)).ToArray();
        if (present.Length == 0)
        {
            return FontVerificationStatus.NotApplicable;
        }

        if (!Has(face, "COLR") || !Has(face, "CPAL") || present.Length != 2)
        {
            return FontVerificationStatus.Unknown;
        }

        ReadOnlySpan<byte> colr = Get(face, "COLR").Data.Span;
        ReadOnlySpan<byte> cpal = Get(face, "CPAL").Data.Span;
        if (colr.Length < 14 || cpal.Length < 12)
        {
            return FontVerificationStatus.Failed;
        }

        if (Read16(colr, 0) != 0 || Read16(cpal, 0) != 0)
        {
            return FontVerificationStatus.Unknown;
        }

        if (!TryReadOffset(colr, 4, out int baseOffset)
            || !TryReadOffset(colr, 8, out int layerOffset)
            || !TryReadOffset(cpal, 8, out int colorOffset))
        {
            return FontVerificationStatus.Failed;
        }

        int baseCount = Read16(colr, 2);
        int layerCount = Read16(colr, 12);
        int entriesPerPalette = Read16(cpal, 2);
        int paletteCount = Read16(cpal, 4);
        int colorCount = Read16(cpal, 6);
        int glyphCount = GlyphCount(face);
        if (glyphCount <= 0 || baseCount == 0 || layerCount == 0 || entriesPerPalette == 0 || paletteCount == 0
            || baseOffset < 14 || layerOffset < 14 || colorOffset < 12 + (paletteCount * 2)
            || !Fits(colr, baseOffset, baseCount * 6) || !Fits(colr, layerOffset, layerCount * 4)
            || RangesOverlap(baseOffset, baseCount * 6, layerOffset, layerCount * 4)
            || !Fits(cpal, 12, paletteCount * 2) || !Fits(cpal, colorOffset, colorCount * 4))
        {
            return FontVerificationStatus.Failed;
        }

        for (int palette = 0; palette < paletteCount; palette++)
        {
            if (Read16(cpal, 12 + (palette * 2)) + entriesPerPalette > colorCount)
            {
                return FontVerificationStatus.Failed;
            }
        }

        int previousGlyph = -1;
        for (int index = 0; index < baseCount; index++)
        {
            int offset = baseOffset + (index * 6);
            int glyph = Read16(colr, offset);
            int firstLayer = Read16(colr, offset + 2);
            int layers = Read16(colr, offset + 4);
            if (glyph <= previousGlyph || glyph >= glyphCount || layers == 0 || firstLayer + layers > layerCount)
            {
                return FontVerificationStatus.Failed;
            }

            previousGlyph = glyph;
        }

        for (int index = 0; index < layerCount; index++)
        {
            int offset = layerOffset + (index * 4);
            int paletteIndex = Read16(colr, offset + 2);
            if (Read16(colr, offset) >= glyphCount || (paletteIndex != 0xFFFF && paletteIndex >= entriesPerPalette))
            {
                return FontVerificationStatus.Failed;
            }
        }

        return FontVerificationStatus.Passed;
    }

    internal static FontVerificationStatus VerifyAxes(OpenTypeFace face, InspectedFontFace inspected)
    {
        string[] present = VariationTags.Where(tag => Has(face, tag)).ToArray();
        if (present.Length == 0 || (present.Length == 1 && present[0] == "STAT"))
        {
            return FontVerificationStatus.NotApplicable;
        }

        if (!Has(face, "fvar"))
        {
            return FontVerificationStatus.Failed;
        }

        if (inspected.VariationAxes.Count == 0 || inspected.VariationAxes.Select(axis => axis.Tag).Distinct().Count() != inspected.VariationAxes.Count)
        {
            return FontVerificationStatus.Failed;
        }

        if (present.Any(tag => tag is not "fvar" and not "gvar"))
        {
            return FontVerificationStatus.Unknown;
        }

        if (!Has(face, "glyf") || !Has(face, "gvar"))
        {
            return FontVerificationStatus.Unknown;
        }

        ReadOnlySpan<byte> gvar = Get(face, "gvar").Data.Span;
        if (gvar.Length < 20 || Read32(gvar, 0) != 0x00010000)
        {
            return FontVerificationStatus.Failed;
        }

        if (!TryReadOffset(gvar, 8, out int sharedTupleOffset)
            || !TryReadOffset(gvar, 16, out int dataOffset))
        {
            return FontVerificationStatus.Failed;
        }

        int axisCount = Read16(gvar, 4);
        int sharedTupleCount = Read16(gvar, 6);
        int glyphCount = Read16(gvar, 12);
        int flags = Read16(gvar, 14);
        int offsetWidth = (flags & 1) != 0 ? 4 : 2;
        long sharedTupleBytes = (long)sharedTupleCount * axisCount * 2;
        int offsetArrayEnd = 20 + ((glyphCount + 1) * offsetWidth);
        if (axisCount != inspected.VariationAxes.Count || glyphCount != GlyphCount(face)
            || (flags & ~1) != 0 || !Fits(gvar, 20, (glyphCount + 1) * offsetWidth)
            || sharedTupleBytes > int.MaxValue || !Fits(gvar, sharedTupleOffset, (int)sharedTupleBytes)
            || (sharedTupleCount > 0 && (sharedTupleOffset < offsetArrayEnd || sharedTupleOffset + sharedTupleBytes > dataOffset))
            || dataOffset < offsetArrayEnd || dataOffset > gvar.Length)
        {
            return FontVerificationStatus.Failed;
        }

        uint previous = 0;
        for (int index = 0; index <= glyphCount; index++)
        {
            uint relative = offsetWidth == 4 ? Read32(gvar, 20 + (index * 4)) : (uint)(Read16(gvar, 20 + (index * 2)) * 2);
            if (relative < previous || relative > gvar.Length - dataOffset)
            {
                return FontVerificationStatus.Failed;
            }

            previous = relative;
        }

        // A nonempty glyph variation record also contains packed point and delta
        // streams. The header check above does not establish that those decode.
        return previous == 0 ? FontVerificationStatus.Passed : FontVerificationStatus.Unknown;
    }

    private static FontVerificationStatus ValidateLayoutTable(ReadOnlySpan<byte> table, bool substitution, int glyphCount)
    {
        int extensionType = substitution ? 7 : 9;
        if (table.Length < 10 || Read32(table, 0) is not 0x00010000 and not 0x00010001)
        {
            return FontVerificationStatus.Failed;
        }

        bool hasUnverifiedContent = table.Length >= 14 && Read32(table, 0) == 0x00010001 && Read32(table, 10) != 0;
        if (Read32(table, 0) == 0x00010001
            && (table.Length < 14
                || !TryReadOffset(table, 10, out int variationsOffset)
                || (variationsOffset != 0 && !Fits(table, variationsOffset, 4))))
        {
            return FontVerificationStatus.Failed;
        }

        int scriptOffset = Read16(table, 4);
        int featureOffset = Read16(table, 6);
        int lookupOffset = Read16(table, 8);
        if (!Fits(table, scriptOffset, 2) || !Fits(table, featureOffset, 2) || !Fits(table, lookupOffset, 2))
        {
            return FontVerificationStatus.Failed;
        }

        int scriptCount = Read16(table, scriptOffset);
        int featureCount = Read16(table, featureOffset);
        int lookupCount = Read16(table, lookupOffset);
        if (!Fits(table, scriptOffset + 2, scriptCount * 6)
            || !Fits(table, featureOffset + 2, featureCount * 6)
            || !Fits(table, lookupOffset + 2, lookupCount * 2))
        {
            return FontVerificationStatus.Failed;
        }

        for (int index = 0; index < scriptCount; index++)
        {
            int script = scriptOffset + Read16(table, scriptOffset + 2 + (index * 6) + 4);
            if (!Fits(table, script, 4))
            {
                return FontVerificationStatus.Failed;
            }

            int languageCount = Read16(table, script + 2);
            if (!Fits(table, script + 4, languageCount * 6))
            {
                return FontVerificationStatus.Failed;
            }

            int defaultLanguage = Read16(table, script);
            if (defaultLanguage != 0 && !ValidateLanguage(table, script + defaultLanguage, featureCount))
            {
                return FontVerificationStatus.Failed;
            }

            for (int language = 0; language < languageCount; language++)
            {
                if (!ValidateLanguage(table, script + Read16(table, script + 4 + (language * 6) + 4), featureCount))
                {
                    return FontVerificationStatus.Failed;
                }
            }
        }

        for (int index = 0; index < featureCount; index++)
        {
            int feature = featureOffset + Read16(table, featureOffset + 2 + (index * 6) + 4);
            if (!Fits(table, feature, 4))
            {
                return FontVerificationStatus.Failed;
            }

            int count = Read16(table, feature + 2);
            if (!Fits(table, feature + 4, count * 2))
            {
                return FontVerificationStatus.Failed;
            }

            for (int lookup = 0; lookup < count; lookup++)
            {
                if (Read16(table, feature + 4 + (lookup * 2)) >= lookupCount)
                {
                    return FontVerificationStatus.Failed;
                }
            }
        }

        for (int index = 0; index < lookupCount; index++)
        {
            int lookup = lookupOffset + Read16(table, lookupOffset + 2 + (index * 2));
            if (!Fits(table, lookup, 6))
            {
                return FontVerificationStatus.Failed;
            }

            int type = Read16(table, lookup);
            int lookupFlags = Read16(table, lookup + 2);
            int subtableCount = Read16(table, lookup + 4);
            if (type == 0 || type > extensionType || !Fits(table, lookup + 6, subtableCount * 2))
            {
                return FontVerificationStatus.Failed;
            }

            if ((lookupFlags & 0x00E0) != 0
                || ((lookupFlags & 0x0010) != 0 && !Fits(table, lookup + 6 + (subtableCount * 2), 2)))
            {
                return FontVerificationStatus.Failed;
            }

            if ((lookupFlags & 0xFF10) != 0)
            {
                // Mark attachment classes and filtering sets refer to GDEF data.
                hasUnverifiedContent = true;
            }

            for (int subtableIndex = 0; subtableIndex < subtableCount; subtableIndex++)
            {
                int relativeOffset = Read16(table, lookup + 6 + (subtableIndex * 2));
                int minimumOffset = 6 + (subtableCount * 2) + ((lookupFlags & 0x0010) != 0 ? 2 : 0);
                int subtable = lookup + relativeOffset;
                if (relativeOffset < minimumOffset || !Fits(table, subtable, type == extensionType ? 8 : 2))
                {
                    return FontVerificationStatus.Failed;
                }

                if (type == extensionType && (Read16(table, subtable) != 1
                    || !TryReadOffset(table, subtable + 4, out int extensionOffset)
                    || extensionOffset < 8
                    || (long)subtable + extensionOffset > int.MaxValue
                    || !Fits(table, subtable + extensionOffset, 2)))
                {
                    return FontVerificationStatus.Failed;
                }

                // All other lookup kinds, including extension lookups, need their own
                // subtable parser before they can earn the completeness score.
                if (type != 1 || glyphCount <= 0)
                {
                    hasUnverifiedContent = true;
                    continue;
                }

                FontVerificationStatus subtableResult = substitution
                    ? ValidateSingleSubstitution(table, subtable, glyphCount)
                    : ValidateSinglePositioning(table, subtable, glyphCount);
                if (subtableResult == FontVerificationStatus.Failed)
                {
                    return FontVerificationStatus.Failed;
                }

                if (subtableResult == FontVerificationStatus.Unknown)
                {
                    hasUnverifiedContent = true;
                }
            }
        }

        return hasUnverifiedContent ? FontVerificationStatus.Unknown : FontVerificationStatus.Passed;
    }

    private static FontVerificationStatus ValidateSingleSubstitution(ReadOnlySpan<byte> table, int offset, int glyphCount)
    {
        if (!Fits(table, offset, 6))
        {
            return FontVerificationStatus.Failed;
        }

        int format = Read16(table, offset);
        int coverageOffset = Read16(table, offset + 2);
        if (coverageOffset < (format == 2 ? 6 + (Read16(table, offset + 4) * 2) : 6)
            || !Fits(table, offset + coverageOffset, 4))
        {
            return FontVerificationStatus.Failed;
        }

        List<int>? glyphs = ReadCoverage(table, offset + coverageOffset, glyphCount);
        if (glyphs is null)
        {
            return FontVerificationStatus.Failed;
        }

        if (format == 1)
        {
            int delta = unchecked((short)Read16(table, offset + 4));
            return glyphs.All(glyph => ((glyph + delta) & 0xFFFF) < glyphCount)
                ? FontVerificationStatus.Passed : FontVerificationStatus.Failed;
        }

        if (format != 2)
        {
            return FontVerificationStatus.Failed;
        }

        int count = Read16(table, offset + 4);
        if (count != glyphs.Count || !Fits(table, offset + 6, count * 2))
        {
            return FontVerificationStatus.Failed;
        }

        for (int index = 0; index < count; index++)
        {
            if (Read16(table, offset + 6 + (index * 2)) >= glyphCount)
            {
                return FontVerificationStatus.Failed;
            }
        }

        return FontVerificationStatus.Passed;
    }

    private static FontVerificationStatus ValidateSinglePositioning(ReadOnlySpan<byte> table, int offset, int glyphCount)
    {
        if (!Fits(table, offset, 6))
        {
            return FontVerificationStatus.Failed;
        }

        int format = Read16(table, offset);
        int coverageOffset = Read16(table, offset + 2);
        int valueFormat = Read16(table, offset + 4);
        int valueSize = System.Numerics.BitOperations.PopCount((uint)(valueFormat & 0xF)) * 2;
        int headerSize = format == 1 ? 6 + valueSize : 8;
        if (coverageOffset < headerSize || !Fits(table, offset + coverageOffset, 4))
        {
            return FontVerificationStatus.Failed;
        }

        List<int>? glyphs = ReadCoverage(table, offset + coverageOffset, glyphCount);
        if (glyphs is null)
        {
            return FontVerificationStatus.Failed;
        }

        if ((valueFormat & 0xFF00) != 0)
        {
            return FontVerificationStatus.Failed;
        }

        if ((valueFormat & 0xF0) != 0)
        {
            return FontVerificationStatus.Unknown;
        }

        if (format == 1)
        {
            return Fits(table, offset + 6, valueSize) ? FontVerificationStatus.Passed : FontVerificationStatus.Failed;
        }

        if (format != 2)
        {
            return FontVerificationStatus.Failed;
        }

        if (!Fits(table, offset + 6, 2))
        {
            return FontVerificationStatus.Failed;
        }

        int count = Read16(table, offset + 6);
        if (coverageOffset < 8 + (count * valueSize))
        {
            return FontVerificationStatus.Failed;
        }

        return count == glyphs.Count && Fits(table, offset + 8, count * valueSize)
            ? FontVerificationStatus.Passed : FontVerificationStatus.Failed;
    }

    private static List<int>? ReadCoverage(ReadOnlySpan<byte> table, int offset, int glyphCount)
    {
        if (!Fits(table, offset, 4))
        {
            return null;
        }

        int format = Read16(table, offset);
        int count = Read16(table, offset + 2);
        var glyphs = new List<int>();
        if (format == 1 && Fits(table, offset + 4, count * 2))
        {
            for (int index = 0; index < count; index++)
            {
                int glyph = Read16(table, offset + 4 + (index * 2));
                if (glyph >= glyphCount || (glyphs.Count > 0 && glyph <= glyphs[^1]))
                {
                    return null;
                }

                glyphs.Add(glyph);
            }

            return glyphs;
        }

        if (format == 2 && Fits(table, offset + 4, count * 6))
        {
            for (int index = 0; index < count; index++)
            {
                int record = offset + 4 + (index * 6);
                int start = Read16(table, record);
                int end = Read16(table, record + 2);
                int coverageIndex = Read16(table, record + 4);
                if (start > end || end >= glyphCount || coverageIndex != glyphs.Count
                    || (glyphs.Count > 0 && start <= glyphs[^1]))
                {
                    return null;
                }

                for (int glyph = start; glyph <= end; glyph++)
                {
                    glyphs.Add(glyph);
                }
            }

            return glyphs;
        }

        return null;
    }

    private static bool ValidateLanguage(ReadOnlySpan<byte> table, int offset, int featureCount)
    {
        if (!Fits(table, offset, 6))
        {
            return false;
        }

        int requiredFeature = Read16(table, offset + 2);
        int count = Read16(table, offset + 4);
        if ((requiredFeature != 0xFFFF && requiredFeature >= featureCount) || !Fits(table, offset + 6, count * 2))
        {
            return false;
        }

        for (int index = 0; index < count; index++)
        {
            if (Read16(table, offset + 6 + (index * 2)) >= featureCount)
            {
                return false;
            }
        }

        return true;
    }

    private static int GlyphCount(OpenTypeFace face)
    {
        return Has(face, "maxp") && Get(face, "maxp").Length >= 6 ? Read16(Get(face, "maxp").Data.Span, 4) : -1;
    }

    private static bool Has(OpenTypeFace face, string tag) => face.Tables.ContainsKey(OpenTypeTag.FromString(tag));

    private static OpenTypeTable Get(OpenTypeFace face, string tag) => face.Tables[OpenTypeTag.FromString(tag)];

    private static bool Fits(ReadOnlySpan<byte> data, int offset, int length) => offset >= 0 && length >= 0 && offset <= data.Length - length;

    private static bool RangesOverlap(int firstOffset, int firstLength, int secondOffset, int secondLength)
        => firstOffset < secondOffset + secondLength && secondOffset < firstOffset + firstLength;

    private static bool TryReadOffset(ReadOnlySpan<byte> data, int offset, out int value)
    {
        uint raw = Read32(data, offset);
        value = raw <= int.MaxValue ? (int)raw : 0;
        return raw <= int.MaxValue;
    }

    private static ushort Read16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));

    private static uint Read32(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4));
}
