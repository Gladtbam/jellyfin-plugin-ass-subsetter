using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

/// <summary>Separates verified field semantics without changing strict byte evidence.</summary>
internal static class FontSemanticCanonicalizer
{
    internal static IReadOnlyList<KeyValuePair<FontSemanticGroup, OpenTypeTable>> Build(OpenTypeFace face, out bool complete)
    {
        var result = new List<KeyValuePair<FontSemanticGroup, OpenTypeTable>>();
        InspectedFontFace inspected = FontFaceInspector.Inspect(face);
        bool canonicalGlyphs = FontGlyphCanonicalizer.TryBuild(face, out byte[] outlines, out byte[] instructions);
        complete = inspected.IsClassifiable && inspected.IsMappingComplete
            && (canonicalGlyphs || !face.Tables.ContainsKey(OpenTypeTag.FromString("glyf")));
        foreach (OpenTypeTable table in face.Tables.Values.OrderBy(table => table.Tag))
        {
            string tag = OpenTypeTag.ToString(table.Tag);
            byte[] data = table.Data.ToArray();
            switch (tag)
            {
                case "head" when data.Length >= 54:
                    Add(result, FontSemanticGroup.Metadata, table, data.AsSpan(4, 4).ToArray().Concat(data.AsSpan(20, 16).ToArray()).ToArray());
                    data.AsSpan(4, 8).Clear();
                    data.AsSpan(20, 16).Clear();
                    if (canonicalGlyphs)
                    {
                        data.AsSpan(50, 2).Clear();
                    }

                    break;
                case "OS/2" when data.Length >= 62:
                    Add(result, FontSemanticGroup.Metadata, table, data.AsSpan(58, 4).ToArray());
                    data.AsSpan(58, 4).Clear();
                    break;
                case "maxp" when canonicalGlyphs && data.Length == 32 && BinaryPrimitives.ReadUInt32BigEndian(data) == 0x10000:
                    Add(result, FontSemanticGroup.Hinting, table, data.AsSpan(14, 14).ToArray());
                    data.AsSpan(14, 14).Clear();
                    break;
                case "glyf" when canonicalGlyphs:
                    Add(result, FontSemanticGroup.Hinting, table, instructions);
                    data = outlines;
                    break;
                case "loca" when canonicalGlyphs:
                    continue;
                case "cmap" when inspected.IsClassifiable && inspected.IsMappingComplete:
                    data = CanonicalMappings(inspected);
                    break;
            }

            Add(result, FontFingerprintBuilder.Classify(table.Tag), table, data);
        }

        return result;
    }

    private static byte[] CanonicalMappings(InspectedFontFace inspected)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(inspected.UnicodeMappings.Count);
        foreach (KeyValuePair<uint, ushort> mapping in inspected.UnicodeMappings.OrderBy(pair => pair.Key))
        {
            writer.Write(mapping.Key);
            writer.Write(mapping.Value);
        }

        writer.Write(inspected.VariationMappings.Count);
        foreach (UnicodeVariationMapping mapping in inspected.VariationMappings.OrderBy(mapping => mapping.Selector).ThenBy(mapping => mapping.CodePoint))
        {
            writer.Write(mapping.Selector);
            writer.Write(mapping.CodePoint);
            writer.Write(mapping.GlyphId ?? inspected.UnicodeMappings.GetValueOrDefault(mapping.CodePoint));
        }

        writer.Write(inspected.VariationSelectors.Count);
        foreach (uint selector in inspected.VariationSelectors)
        {
            writer.Write(selector);
        }

        return stream.ToArray();
    }

    private static void Add(List<KeyValuePair<FontSemanticGroup, OpenTypeTable>> result, FontSemanticGroup group, OpenTypeTable table, byte[] data)
        => result.Add(new KeyValuePair<FontSemanticGroup, OpenTypeTable>(group, table with { Data = data, Length = data.Length }));
}
