using System;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <summary>Summary fields and the group count of a persisted analysis report.</summary>
/// <param name="Id">The persisted batch identifier.</param>
/// <param name="GenerationId">The analyzed inventory generation.</param>
/// <param name="Status">The report lifecycle state.</param>
/// <param name="EvidenceVersion">The version of the analysis evidence.</param>
/// <param name="StartedUtc">The report start time.</param>
/// <param name="CompletedUtc">The completion time for terminal reports.</param>
/// <param name="CandidateCount">The number of candidate pairs.</param>
/// <param name="ComparisonCount">The number of deep comparisons.</param>
/// <param name="ReclaimableBytes">The proven reclaimable byte count.</param>
/// <param name="GroupCount">The number of direct duplicate groups.</param>
public sealed record AnalysisBatchSummary(
    long Id,
    long GenerationId,
    AnalysisBatchStatus Status,
    int EvidenceVersion,
    DateTimeOffset StartedUtc,
    DateTimeOffset? CompletedUtc,
    long CandidateCount,
    long ComparisonCount,
    long ReclaimableBytes,
    int GroupCount);
