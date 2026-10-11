using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>
/// Durable state of one resumable file-maintenance batch.
/// </summary>
public sealed record MaintenanceBatch
{
    /// <summary>Gets the persisted batch identifier, or zero before insertion.</summary>
    public long Id { get; init; }

    /// <summary>Gets the immutable operation request identifier.</summary>
    public long OperationRequestId { get; init; }

    /// <summary>Gets the batch operation kind.</summary>
    public FontOperationKind Kind { get; init; }

    /// <summary>Gets the batch lifecycle state.</summary>
    public MaintenanceBatchStatus Status { get; init; }

    /// <summary>Gets the start time.</summary>
    public DateTimeOffset StartedUtc { get; init; }

    /// <summary>Gets the terminal completion time.</summary>
    public DateTimeOffset? CompletedUtc { get; init; }

    /// <summary>Gets the durable batch summary.</summary>
    public required string SummaryJson { get; init; }

    /// <summary>Gets the durable per-file quarantine outcomes.</summary>
    public IReadOnlyList<QuarantineItemRecord> Items { get; init; } = [];

    /// <summary>Gets durable filename-normalization outcomes, separate from quarantine items.</summary>
    public IReadOnlyList<RenameItemRecord> RenameItems { get; init; } = [];
}
