using System.Collections.Generic;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Api;

/// <summary>Describes safe persisted pair relationship evidence.</summary>
/// <param name="LeftFaceId">The lower persisted Face identifier.</param>
/// <param name="RightFaceId">The higher persisted Face identifier.</param>
/// <param name="RelationshipKind">The persisted relationship classification.</param>
/// <param name="ReplacementDirection">The directional replacement conclusion.</param>
/// <param name="EvidenceVersion">The evidence schema version.</param>
/// <param name="EvidenceKeys">The persisted structured evidence keys.</param>
/// <param name="DifferingCodePoints">The persisted differing glyph code points.</param>
/// <param name="LeftCanReplaceRight">Whether the left Face directly replaces the right Face.</param>
/// <param name="RightCanReplaceLeft">Whether the right Face directly replaces the left Face.</param>
/// <param name="CollectionFaceImpact">The safe TTC/OTC collection-Face impact, if recorded.</param>
/// <param name="DifferenceTypes">The structured persisted difference classifications.</param>
public sealed record PairEvidenceDto(long LeftFaceId, long RightFaceId, FontRelationshipKind RelationshipKind, FontReplacementDirection ReplacementDirection, int EvidenceVersion, IReadOnlyList<string> EvidenceKeys, IReadOnlyList<uint> DifferingCodePoints, bool LeftCanReplaceRight, bool RightCanReplaceLeft, string? CollectionFaceImpact, IReadOnlyList<string> DifferenceTypes)
{
    /// <summary>Gets the left Face and its exact physical file.</summary>
    public FontFaceComparisonDto? LeftFace { get; init; }

    /// <summary>Gets the right Face and its exact physical file.</summary>
    public FontFaceComparisonDto? RightFace { get; init; }

    /// <summary>Gets names recorded for both Faces, allowing the administrator to identify the collision.</summary>
    public IReadOnlyList<string> SharedNames { get; init; } = [];

    /// <summary>Gets the reason left-side evidence could not be verified.</summary>
    public string? LeftDiagnostic { get; init; }

    /// <summary>Gets the reason right-side evidence could not be verified.</summary>
    public string? RightDiagnostic { get; init; }
}
