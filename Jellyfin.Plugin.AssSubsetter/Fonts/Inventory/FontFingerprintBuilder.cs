using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

/// <summary>
/// Builds container-independent SHA-256 evidence from validated OpenType table views.
/// </summary>
public static class FontFingerprintBuilder
{
    private static readonly uint HeadTag = OpenTypeTag.FromString("head");

    /// <summary>Builds strict and semantic digests for one validated Face.</summary>
    /// <param name="face">The validated Face table directory.</param>
    /// <returns>The canonical Face fingerprints.</returns>
    public static FontFaceFingerprints BuildFace(OpenTypeFace face)
    {
        ArgumentNullException.ThrowIfNull(face);
        OpenTypeTable[] orderedTables = face.Tables.Values.OrderBy(table => table.Tag).ToArray();
        ReadOnlyMemory<byte> strict = HashTables(orderedTables);
        var semantic = new Dictionary<FontSemanticGroup, ReadOnlyMemory<byte>>();
        IReadOnlyList<KeyValuePair<FontSemanticGroup, OpenTypeTable>> semanticTables = FontSemanticCanonicalizer.Build(face, out bool complete);
        foreach (IGrouping<FontSemanticGroup, KeyValuePair<FontSemanticGroup, OpenTypeTable>> group in semanticTables.GroupBy(table => table.Key))
        {
            semantic.Add(group.Key, HashTables(group.Select(table => table.Value).OrderBy(table => table.Tag)));
        }

        return new FontFaceFingerprints(strict, semantic) { IsSemanticEvidenceComplete = complete };
    }

    private static ReadOnlyMemory<byte> HashTables(IEnumerable<OpenTypeTable> tables)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> header = stackalloc byte[8];
        foreach (OpenTypeTable table in tables)
        {
            BinaryPrimitives.WriteUInt32BigEndian(header.Slice(0, 4), table.Tag);
            BinaryPrimitives.WriteUInt32BigEndian(header.Slice(4, 4), checked((uint)table.Length));
            hash.AppendData(header);
            AppendCanonicalTable(hash, table);
        }

        return hash.GetHashAndReset();
    }

    private static void AppendCanonicalTable(IncrementalHash hash, OpenTypeTable table)
    {
        ReadOnlySpan<byte> data = table.Data.Span;
        if (table.Tag != HeadTag || data.Length < 12)
        {
            hash.AppendData(data);
            return;
        }

        hash.AppendData(data.Slice(0, 8));
        Span<byte> zeroAdjustment = stackalloc byte[4];
        zeroAdjustment.Clear();
        hash.AppendData(zeroAdjustment);
        hash.AppendData(data.Slice(12));
    }

    internal static FontSemanticGroup Classify(uint tag)
    {
        string value = OpenTypeTag.ToString(tag);
        return value switch
        {
            "name" or "DSIG" or "meta" => FontSemanticGroup.Metadata,
            "cmap" => FontSemanticGroup.Mapping,
            "GDEF" or "GPOS" or "GSUB" or "BASE" or "JSTF" or "MATH" or "kern" or "kerx" or "morx" or "feat" => FontSemanticGroup.Layout,
            "cvt " or "fpgm" or "prep" or "gasp" or "LTSH" or "VDMX" or "hdmx" => FontSemanticGroup.Hinting,
            "COLR" or "CPAL" or "CBDT" or "CBLC" or "sbix" or "SVG " or "EBDT" or "EBLC" => FontSemanticGroup.ColorAndBitmap,
            "fvar" or "gvar" or "avar" or "HVAR" or "VVAR" or "MVAR" or "STAT" or "cvar" => FontSemanticGroup.Variation,
            "glyf" or "loca" or "CFF " or "CFF2" or "hmtx" or "vmtx" or "hhea" or "vhea" or "maxp" or "head" or "OS/2" or "post" => FontSemanticGroup.OutlinesAndMetrics,
            _ => FontSemanticGroup.OtherFunctional
        };
    }
}
