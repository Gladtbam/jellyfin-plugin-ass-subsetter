using System.Collections.Generic;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>
/// Memory-only Face information consumed by subtitle font lookup.
/// </summary>
/// <param name="FaceId">Persisted Face identifier.</param>
/// <param name="Path">Physical font path.</param>
/// <param name="FaceIndex">Zero-based Face index.</param>
/// <param name="FamilyName">Preferred family name.</param>
/// <param name="StyleName">Preferred style name.</param>
/// <param name="Names">Lookup aliases.</param>
/// <param name="Weight">OpenType weight class.</param>
/// <param name="Width">OpenType width class.</param>
/// <param name="Slant">Normalized slant.</param>
/// <param name="IsItalic">Whether the Face is italic.</param>
/// <param name="IsFixedPitch">Whether the Face is fixed pitch.</param>
/// <param name="Technology">Font technology label.</param>
/// <param name="RetentionScore">Fixed retention score.</param>
/// <param name="IsRuntimePreferred">Whether runtime lookup should prefer the Face.</param>
public sealed record RuntimeFontFace(
    long FaceId,
    string Path,
    int FaceIndex,
    string FamilyName,
    string StyleName,
    IReadOnlyList<string> Names,
    int Weight,
    int Width,
    int Slant,
    bool IsItalic,
    bool IsFixedPitch,
    string Technology,
    int RetentionScore,
    bool IsRuntimePreferred)
{
    /// <summary>Gets typed OpenType names used to distinguish family aliases from Face-specific names.</summary>
    public IReadOnlyList<OpenTypeNameRecord> NameRecords { get; init; } = [];

    /// <summary>Gets ordinary Unicode scalar coverage; empty means coverage was unavailable or no scalar mappings were recorded.</summary>
    public IReadOnlyList<UnicodeRange> UnicodeCoverage { get; init; } = [];
}
