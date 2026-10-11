using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Native;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;

/// <summary>Verifies quality evidence and distinguishes absent or unsupported optional capabilities.</summary>
internal static class FontQualityVerifier
{
    internal static Dictionary<string, FontVerificationStatus> Verify(
        byte[] data,
        OpenTypeFace face,
        InspectedFontFace inspected,
        bool skiaSucceeded,
        Func<byte[], int, InspectedFontFace, FontVerificationStatus>? verifyFreeType = null,
        Func<byte[], InspectedFontFace, FontVerificationStatus>? verifyLibass = null)
    {
        return new Dictionary<string, FontVerificationStatus>(StringComparer.Ordinal)
        {
            ["checksums"] = Status(face.Tables.Values.All(HasValidChecksum)),
            ["embeddingRights"] = EmbeddingRights(face),
            ["skia"] = Status(skiaSucceeded),
            ["harfbuzz"] = VerifyHarfBuzz(data, face.FaceIndex, inspected),
            ["freetype"] = (verifyFreeType ?? FreeTypeFontVerifier.Verify)(data, face.FaceIndex, inspected),
            ["libass"] = (verifyLibass ?? LibassFontVerifier.Verify)(data, inspected),
            ["layout"] = OpenTypeFeatureVerifier.VerifyLayout(face),
            ["vertical"] = VerifyVertical(face),
            ["color"] = OpenTypeFeatureVerifier.VerifyColor(face),
            ["axes"] = OpenTypeFeatureVerifier.VerifyAxes(face, inspected)
        };
    }

    private static bool HasValidChecksum(OpenTypeTable table)
    {
        ReadOnlySpan<byte> data = table.Data.Span;
        uint checksum = 0;
        bool head = table.Tag == OpenTypeTag.FromString("head");
        for (int offset = 0; offset < data.Length; offset += 4)
        {
            uint word = 0;
            for (int index = 0; index < 4; index++)
            {
                int position = offset + index;
                byte value = position < data.Length && !(head && position is >= 8 and < 12) ? data[position] : (byte)0;
                word = (word << 8) | value;
            }

            checksum = unchecked(checksum + word);
        }

        return checksum == table.Checksum;
    }

    private static FontVerificationStatus EmbeddingRights(OpenTypeFace face)
    {
        if (!face.Tables.TryGetValue(OpenTypeTag.FromString("OS/2"), out OpenTypeTable? table) || table.Length < 10)
        {
            return FontVerificationStatus.Unknown;
        }

        // Only unrestricted installable embedding receives credit; restricted or reserved flags do not.
        return Status(BinaryPrimitives.ReadUInt16BigEndian(table.Data.Span.Slice(8, 2)) == 0);
    }

    private static FontVerificationStatus VerifyVertical(OpenTypeFace face)
    {
        bool hasHeader = face.Tables.TryGetValue(OpenTypeTag.FromString("vhea"), out OpenTypeTable? header);
        bool hasMetrics = face.Tables.TryGetValue(OpenTypeTag.FromString("vmtx"), out OpenTypeTable? metrics);
        if (!hasHeader && !hasMetrics)
        {
            return FontVerificationStatus.NotApplicable;
        }

        if (!hasHeader || !hasMetrics || header!.Length < 36
            || !face.Tables.TryGetValue(OpenTypeTag.FromString("maxp"), out OpenTypeTable? maxp)
            || maxp.Length < 6)
        {
            return FontVerificationStatus.Failed;
        }

        ReadOnlySpan<byte> vhea = header.Data.Span;
        int glyphCount = BinaryPrimitives.ReadUInt16BigEndian(maxp.Data.Span.Slice(4, 2));
        int verticalMetricCount = BinaryPrimitives.ReadUInt16BigEndian(vhea.Slice(34, 2));
        int expectedLength = (verticalMetricCount * 4) + ((glyphCount - verticalMetricCount) * 2);
        return Status(BinaryPrimitives.ReadUInt32BigEndian(vhea) is 0x00010000 or 0x00011000
            && verticalMetricCount > 0
            && verticalMetricCount <= glyphCount
            && metrics!.Length == expectedLength);
    }

    private static FontVerificationStatus VerifyHarfBuzz(byte[] data, int faceIndex, InspectedFontFace inspected)
    {
        if (!inspected.IsClassifiable || inspected.UnicodeMappings.Count == 0)
        {
            return FontVerificationStatus.Failed;
        }

        uint scalar = inspected.UnicodeMappings.Keys.Min();
        byte[]? subset = HarfBuzzSubsetNative.SubsetFont(data, checked((uint)faceIndex), [scalar], NullLogger.Instance);
        if (subset is null || subset.Length == 0)
        {
            return FontVerificationStatus.Failed;
        }

        try
        {
            OpenTypeContainer container = OpenTypeFontReader.Read(subset);
            InspectedFontFace parsed = FontFaceInspector.Inspect(container.Faces[0]);
            return Status(parsed.IsClassifiable && parsed.UnicodeMappings.ContainsKey(scalar));
        }
        catch (InvalidDataException)
        {
            return FontVerificationStatus.Failed;
        }
    }

    private static FontVerificationStatus Status(bool passed) => passed ? FontVerificationStatus.Passed : FontVerificationStatus.Failed;
}
