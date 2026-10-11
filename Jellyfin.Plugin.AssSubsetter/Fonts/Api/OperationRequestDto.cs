using System;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Api;

/// <summary>Describes an immutable operation request without its raw payload document.</summary>
/// <param name="Id">The persisted request identifier.</param>
/// <param name="Kind">The requested operation kind.</param>
/// <param name="Status">The request lifecycle state.</param>
/// <param name="AnalysisBatchId">The source analysis batch when applicable.</param>
/// <param name="CreatedUtc">The immutable creation time.</param>
/// <param name="ClaimedUtc">The scheduler claim time.</param>
/// <param name="CompletedUtc">The terminal completion time.</param>
/// <param name="Error">The safe terminal error, if any.</param>
public sealed record OperationRequestDto(long Id, FontOperationKind Kind, OperationRequestStatus Status, long? AnalysisBatchId, DateTimeOffset CreatedUtc, DateTimeOffset? ClaimedUtc, DateTimeOffset? CompletedUtc, string? Error)
{
    /// <summary>Gets the latest complete scan outcome.</summary>
    public FontScanOutcomeDto? ScanResult { get; init; }
}
