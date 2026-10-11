using System;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>Durable state and immutable evidence for one quarantined physical Font File.</summary>
public sealed record QuarantineItemRecord
{
    /// <summary>Gets the persisted item identifier, or zero before insertion.</summary>
    public long Id { get; init; }

    /// <summary>Gets the containing maintenance batch identifier.</summary>
    public long MaintenanceBatchId { get; init; }

    /// <summary>Gets the selected physical Font File identifier.</summary>
    public long FileId { get; init; }

    /// <summary>Gets the immutable original path stored as data.</summary>
    public required string SourcePath { get; init; }

    /// <summary>Gets the generated batch-local staging path.</summary>
    public required string StagingPath { get; init; }

    /// <summary>Gets the generated batch-local final quarantine path.</summary>
    public required string QuarantinePath { get; init; }

    /// <summary>Gets the immutable source SHA-256 digest.</summary>
    public required ReadOnlyMemory<byte> Sha256 { get; init; }

    /// <summary>Gets the immutable source length.</summary>
    public long Size { get; init; }

    /// <summary>Gets the item lifecycle state.</summary>
    public QuarantineItemStatus Status { get; init; }

    /// <summary>Gets the successful quarantine time.</summary>
    public DateTimeOffset? QuarantinedUtc { get; init; }

    /// <summary>Gets the earliest fixed expiry time.</summary>
    public DateTimeOffset? ExpiresUtc { get; init; }

    /// <summary>Gets the successful restore time.</summary>
    public DateTimeOffset? RestoredUtc { get; init; }

    /// <summary>Gets the durable item diagnostic.</summary>
    public string? Error { get; init; }
}
