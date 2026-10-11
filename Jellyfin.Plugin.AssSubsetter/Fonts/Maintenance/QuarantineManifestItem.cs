using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

/// <summary>Contains immutable evidence and durable state for one manifest item.</summary>
public sealed record QuarantineManifestItem
{
    /// <summary>Gets the selected physical-file identifier.</summary>
    public long FileId { get; init; }

    /// <summary>Gets the original source path stored only as manifest data.</summary>
    public required string SourcePath { get; init; }

    /// <summary>Gets the verified original native identity required for source safety checks.</summary>
    public required string SourcePhysicalIdentity { get; init; }

    /// <summary>Gets the generated staging path.</summary>
    public required string StagingPath { get; init; }

    /// <summary>Gets the generated final quarantine path.</summary>
    public required string QuarantinePath { get; init; }

    /// <summary>Gets the immutable source length.</summary>
    public long Size { get; init; }

    /// <summary>Gets the immutable source modification time.</summary>
    public DateTimeOffset ModifiedUtc { get; init; }

    /// <summary>Gets the immutable source SHA-256 digest.</summary>
    public required ReadOnlyMemory<byte> Sha256 { get; init; }

    /// <summary>Gets every retained replacement that must remain valid.</summary>
    public required IReadOnlyList<QuarantineReplacementEvidence> Replacements { get; init; }

    /// <summary>Gets one persisted directional direct-replacement proof for every Face in the source file.</summary>
    public required IReadOnlyList<QuarantineFaceReplacementProof> FaceReplacementProofs { get; init; }

    /// <summary>Gets the durable item state.</summary>
    public QuarantineItemStatus Status { get; init; }

    /// <summary>Gets the successful quarantine time.</summary>
    public DateTimeOffset? QuarantinedUtc { get; init; }

    /// <summary>Gets the fixed earliest expiry time.</summary>
    public DateTimeOffset? ExpiresUtc { get; init; }

    /// <summary>Gets the successful restore time.</summary>
    public DateTimeOffset? RestoredUtc { get; init; }

    /// <summary>Gets the durable diagnostic.</summary>
    public string? Error { get; init; }
}
