using System.Collections.Generic;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <summary>Provides the group and member fields displayed by the management interface.</summary>
/// <param name="AnalysisBatchId">The owning report identifier.</param>
/// <param name="Id">The persisted group identifier.</param>
/// <param name="RetainedFaceId">The retained Face identifier.</param>
/// <param name="ReclaimableBytes">The proven reclaimable byte count.</param>
/// <param name="Members">The decisions and scores of the group members.</param>
public sealed record DuplicateGroupSummary(
    long AnalysisBatchId,
    long Id,
    long RetainedFaceId,
    long ReclaimableBytes,
    IReadOnlyList<DuplicateGroupMemberRecord> Members);
