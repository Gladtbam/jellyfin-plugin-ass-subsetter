using System.Collections.Generic;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Api;

/// <summary>Bounded status page and current analysis availability.</summary>
/// <param name="Current">Current authorization context.</param>
/// <param name="BlockReason">Why new execution is unavailable.</param>
/// <param name="Items">Only the selected page of rows.</param>
/// <param name="Page">One-based page.</param>
/// <param name="PageSize">Bounded page size.</param>
/// <param name="TotalCount">Exact filtered count.</param>
/// <param name="CandidateState">The observable background candidate state.</param>
/// <param name="CanRefreshCandidates">Whether a compatible current analysis can supply a refresh.</param>
public sealed record RenameStatePageDto(FontRenameContextDto? Current, string? BlockReason, IReadOnlyList<RenameStateItemDto> Items, int Page, int PageSize, long TotalCount, FontRenameCandidateStateDto? CandidateState, bool CanRefreshCandidates = false);
