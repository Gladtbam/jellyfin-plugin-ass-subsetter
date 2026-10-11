using System.Collections.Generic;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;

/// <summary>Describes one direct, non-transitive physical-file replacement group.</summary>
/// <param name="RetainedFileId">The physical file retained for this group.</param>
/// <param name="RetainedFaceId">A retained Face that directly participates in the group.</param>
/// <param name="QuarantineFileIds">Physical files directly and completely replaced by the retained file.</param>
/// <param name="ReclaimableBytes">The recoverable bytes represented by this group.</param>
public sealed record DuplicateGroupPlan(
    long RetainedFileId,
    long RetainedFaceId,
    IReadOnlyList<long> QuarantineFileIds,
    long ReclaimableBytes)
{
    /// <summary>Gets every retained physical file required for direct coverage of the quarantined files.</summary>
    public IReadOnlyList<long> RetainedFileIds { get; init; } = [RetainedFileId];
}
