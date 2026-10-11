using System.Collections.Generic;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

/// <summary>
/// Deterministic identity, lookup, coverage, and technical evidence extracted from one font Face.
/// </summary>
public sealed record InspectedFontFace
{
    /// <summary>Gets the source Face index.</summary>
    public int FaceIndex { get; init; }

    /// <summary>Gets a value indicating whether all required identity and mapping evidence was proven.</summary>
    public bool IsClassifiable { get; init; }

    /// <summary>Gets the reason that classification was refused.</summary>
    public string? Error { get; init; }

    /// <summary>Gets the preferred typographic or legacy family name.</summary>
    public required string FamilyName { get; init; }

    /// <summary>Gets the preferred typographic or legacy subfamily name.</summary>
    public required string StyleName { get; init; }

    /// <summary>Gets deterministic runtime lookup aliases.</summary>
    public IReadOnlyList<string> Names { get; init; } = [];

    /// <summary>Gets all decoded name records used as identity evidence.</summary>
    public IReadOnlyList<OpenTypeNameRecord> NameRecords { get; init; } = [];

    /// <summary>Gets the OpenType weight class.</summary>
    public int Weight { get; init; }

    /// <summary>Gets the OpenType width class.</summary>
    public int Width { get; init; }

    /// <summary>Gets the normalized slant value.</summary>
    public int Slant { get; init; }

    /// <summary>Gets a value indicating whether the Face is italic.</summary>
    public bool IsItalic { get; init; }

    /// <summary>Gets a value indicating whether the Face declares fixed-pitch glyphs.</summary>
    public bool IsFixedPitch { get; init; }

    /// <summary>Gets the outline and container technology label.</summary>
    public required string Technology { get; init; }

    /// <summary>Gets the units-per-em value.</summary>
    public int UnitsPerEm { get; init; }

    /// <summary>Gets the normalized version evidence.</summary>
    public string? Version { get; init; }

    /// <summary>Gets the independently parsed <c>head.fontRevision</c> value.</summary>
    public string? HeadRevision { get; init; }

    /// <summary>Gets the four-character OS/2 vendor identifier when available.</summary>
    public string? VendorId { get; init; }

    /// <summary>Gets the number of structurally valid cmap subtables.</summary>
    public int ValidCmapCount { get; init; }

    /// <summary>Gets normalized Unicode scalar-value coverage.</summary>
    public IReadOnlyList<UnicodeRange> UnicodeCoverage { get; init; } = [];

    /// <summary>Gets validated Unicode-to-glyph mappings independent of cmap representation.</summary>
    public IReadOnlyDictionary<uint, ushort> UnicodeMappings { get; init; } = new Dictionary<uint, ushort>();

    /// <summary>Gets a value indicating whether all supplied encoding subtables have supported, consistent semantics.</summary>
    public bool IsMappingComplete { get; init; }

    /// <summary>Gets the supported Unicode variation selectors.</summary>
    public IReadOnlyList<uint> VariationSelectors { get; init; } = [];

    /// <summary>Gets every validated default and non-default Unicode variation mapping.</summary>
    public IReadOnlyList<UnicodeVariationMapping> VariationMappings { get; init; } = [];

    /// <summary>Gets the variable-font axes in source order.</summary>
    public IReadOnlyList<FontVariationAxis> VariationAxes { get; init; } = [];

    /// <summary>Gets a value indicating whether both vertical header and metrics tables are present.</summary>
    public bool HasVerticalMetrics { get; init; }

    /// <summary>Gets a value indicating whether at least one OpenType layout table is present.</summary>
    public bool HasLayoutTables { get; init; }

    /// <summary>Gets a value indicating whether color, SVG, or bitmap glyph data is present.</summary>
    public bool HasColorOrBitmapGlyphs { get; init; }

    /// <summary>Creates a non-actionable result when required evidence cannot be parsed safely.</summary>
    /// <param name="faceIndex">The source Face index.</param>
    /// <param name="reason">The administrator-facing refusal reason.</param>
    /// <returns>An unclassifiable Face result with no fabricated coverage.</returns>
    public static InspectedFontFace Unclassifiable(int faceIndex, string reason)
    {
        return new InspectedFontFace
        {
            FaceIndex = faceIndex,
            IsClassifiable = false,
            Error = reason,
            FamilyName = string.Empty,
            StyleName = string.Empty,
            Technology = string.Empty
        };
    }
}
