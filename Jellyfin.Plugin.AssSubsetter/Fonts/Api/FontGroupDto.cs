using System.Collections.Generic;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Api;

/// <summary>Describes one duplicate group without persisted evidence blobs.</summary>
/// <param name="Id">The persisted group identifier.</param>
/// <param name="AnalysisBatchId">The owning analysis batch identifier.</param>
/// <param name="RetainedFaceId">The Face retained by the group.</param>
/// <param name="ReclaimableBytes">The safely reclaimable byte count.</param>
/// <param name="Members">The retained and quarantine-candidate members.</param>
public sealed record FontGroupDto(long Id, long AnalysisBatchId, long RetainedFaceId, long ReclaimableBytes, IReadOnlyList<FontGroupMemberDto> Members);
