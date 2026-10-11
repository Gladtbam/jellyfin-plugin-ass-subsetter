using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

/// <summary>Provides the immutable recovery document stored beside a quarantine batch.</summary>
public sealed record QuarantineManifest
{
    /// <summary>Gets the manifest schema version.</summary>
    public required int Version { get; init; }

    /// <summary>Gets the current analysis proof version required for maintenance.</summary>
    public required int EvidenceVersion { get; init; }

    /// <summary>Gets the maintenance batch identifier.</summary>
    public long BatchId { get; init; }

    /// <summary>Gets the immutable operation request identifier.</summary>
    public long OperationRequestId { get; init; }

    /// <summary>Gets the immutable operation idempotency identity.</summary>
    public required string OperationIdempotencyKey { get; init; }

    /// <summary>Gets the path-free immutable operation payload.</summary>
    public required string OperationRequestJson { get; init; }

    /// <summary>Gets the source analysis batch identifier.</summary>
    public long AnalysisBatchId { get; init; }

    /// <summary>Gets the manifest creation time.</summary>
    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>Gets the per-file immutable evidence and current durable states.</summary>
    public required IReadOnlyList<QuarantineManifestItem> Items { get; init; }

    internal void ValidateCurrentFormat()
    {
        if (Version != 2)
        {
            throw new InvalidDataException($"Unsupported quarantine manifest version {Version}.");
        }

        if (EvidenceVersion != FontDuplicateAnalyzer.CurrentEvidenceVersion)
        {
            throw new InvalidDataException($"Unsupported quarantine evidence version {EvidenceVersion}.");
        }

        foreach (QuarantineManifestItem item in Items)
        {
            if (string.IsNullOrWhiteSpace(item.SourcePhysicalIdentity))
            {
                throw new InvalidDataException("A quarantine manifest requires every source physical identity.");
            }
        }
    }
}
