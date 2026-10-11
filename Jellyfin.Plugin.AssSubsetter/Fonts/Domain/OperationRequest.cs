using System;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>
/// Immutable request queued by the administrator UI or a scheduled task.
/// </summary>
public sealed record OperationRequest
{
    /// <summary>Gets the persisted request identifier, or zero before insertion.</summary>
    public long Id { get; init; }

    /// <summary>Gets the requested operation.</summary>
    public FontOperationKind Kind { get; init; }

    /// <summary>Gets the request lifecycle state.</summary>
    public OperationRequestStatus Status { get; init; }

    /// <summary>Gets the source analysis batch when one is required.</summary>
    public long? AnalysisBatchId { get; init; }

    /// <summary>Gets the client-provided idempotency key.</summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>Gets the validated request payload.</summary>
    public required string RequestJson { get; init; }

    /// <summary>Gets the creation time.</summary>
    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>Gets the claim time.</summary>
    public DateTimeOffset? ClaimedUtc { get; init; }

    /// <summary>Gets the terminal completion time.</summary>
    public DateTimeOffset? CompletedUtc { get; init; }

    /// <summary>Gets the successful result document, separate from terminal errors.</summary>
    public string? ResultJson { get; init; }

    /// <summary>Gets a terminal error suitable for an administrator report.</summary>
    public string? Error { get; init; }
}
