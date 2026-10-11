using System.Collections.Generic;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>Describes one persisted non-transitive direct replacement group.</summary>
public sealed record DuplicateGroupRecord
{
    /// <summary>Gets the persisted group identifier.</summary>
    public long Id { get; init; }

    /// <summary>Gets the retained Face that anchors direct replacement proof.</summary>
    public long RetainedFaceId { get; init; }

    /// <summary>Gets the safely reclaimable byte count.</summary>
    public long ReclaimableBytes { get; init; }

    /// <summary>Gets the complete group-level evidence document.</summary>
    public required string EvidenceJson { get; init; }

    /// <summary>Gets retained and quarantine-candidate members.</summary>
    public IReadOnlyList<DuplicateGroupMemberRecord> Members { get; init; } = [];
}
