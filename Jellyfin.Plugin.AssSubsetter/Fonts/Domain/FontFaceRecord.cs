using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>
/// Immutable inventory evidence for one Face in a font file.
/// </summary>
public sealed record FontFaceRecord
{
    /// <summary>Gets the database identifier, or zero before persistence.</summary>
    public long Id { get; init; }

    /// <summary>Gets the zero-based Face index within its container.</summary>
    public required int FaceIndex { get; init; }

    /// <summary>Gets the canonical strict-Face SHA-256 digest.</summary>
    public required ReadOnlyMemory<byte> StrictFingerprint { get; init; }

    /// <summary>Gets the preferred family name.</summary>
    public required string FamilyName { get; init; }

    /// <summary>Gets the preferred style name.</summary>
    public required string StyleName { get; init; }

    /// <summary>Gets all lookup aliases in deterministic order.</summary>
    public required IReadOnlyList<string> Names { get; init; }

    /// <summary>Gets typed naming evidence retained for deterministic maintenance planning.</summary>
    public IReadOnlyList<OpenTypeNameRecord> NameRecords { get; init; } = [];

    /// <summary>Gets complete ordinary Unicode scalar coverage from the parsed cmap.</summary>
    public IReadOnlyList<UnicodeRange> UnicodeCoverage { get; init; } = [];

    /// <summary>Gets the OpenType weight class.</summary>
    public int Weight { get; init; }

    /// <summary>Gets the OpenType width class.</summary>
    public int Width { get; init; }

    /// <summary>Gets the normalized slant value.</summary>
    public int Slant { get; init; }

    /// <summary>Gets a value indicating whether the Face is italic.</summary>
    public bool IsItalic { get; init; }

    /// <summary>Gets a value indicating whether the Face is fixed pitch.</summary>
    public bool IsFixedPitch { get; init; }

    /// <summary>Gets the outline/container technology label.</summary>
    public required string Technology { get; init; }

    /// <summary>Gets the units-per-em value.</summary>
    public int UnitsPerEm { get; init; }

    /// <summary>Gets the normalized version text.</summary>
    public string? Version { get; init; }

    /// <summary>Gets the fixed retention score.</summary>
    public int RetentionScore { get; init; }

    /// <summary>Gets a value indicating whether runtime lookup should prefer this Face.</summary>
    public bool IsRuntimePreferred { get; init; }
}
