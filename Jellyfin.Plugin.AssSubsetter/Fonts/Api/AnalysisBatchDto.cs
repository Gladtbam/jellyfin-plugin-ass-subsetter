using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Api;

/// <summary>Describes a persisted immutable analysis report without its raw summary document.</summary>
/// <param name="Id">The persisted analysis batch identifier.</param>
/// <param name="GenerationId">The analyzed inventory generation identifier.</param>
/// <param name="Status">The report lifecycle state.</param>
/// <param name="StartedUtc">The report start time.</param>
/// <param name="CompletedUtc">The terminal completion time.</param>
/// <param name="CandidateCount">The candidate-pair count.</param>
/// <param name="ComparisonCount">The deep-comparison count.</param>
/// <param name="ReclaimableBytes">The proven reclaimable byte count.</param>
/// <param name="GroupCount">The number of direct duplicate groups.</param>
public sealed record AnalysisBatchDto(long Id, long GenerationId, AnalysisBatchStatus Status, DateTimeOffset StartedUtc, DateTimeOffset? CompletedUtc, long CandidateCount, long ComparisonCount, long ReclaimableBytes, int GroupCount)
{
    /// <summary>Gets a value indicating whether this report matches the current publication and analysis inputs.</summary>
    public bool IsCurrent { get; init; }

    /// <summary>Gets concrete reason codes requiring refreshed analysis.</summary>
    public IReadOnlyList<string> StaleReasons { get; init; } = [];
}
