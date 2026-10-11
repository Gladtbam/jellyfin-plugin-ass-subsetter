using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

/// <summary>Persists one directional direct-replacement proof for a quarantined Face.</summary>
/// <param name="TargetFaceId">The Face in the quarantined physical file.</param>
/// <param name="ReplacementFaceId">The directly compatible retained Face.</param>
/// <param name="ReplacementFileId">The retained physical file containing the replacement Face.</param>
/// <param name="RelationshipKind">The automatic-action relationship proved by analysis.</param>
/// <param name="ReplacementDirection">The proved directional replacement boundary.</param>
/// <param name="Technology">The exact matching font technology.</param>
public sealed record QuarantineFaceReplacementProof(
    long TargetFaceId,
    long ReplacementFaceId,
    long ReplacementFileId,
    FontRelationshipKind RelationshipKind,
    FontReplacementDirection ReplacementDirection,
    string Technology);
