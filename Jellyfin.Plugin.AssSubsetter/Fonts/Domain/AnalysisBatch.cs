using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>
/// Immutable duplicate-analysis report and its pair evidence.
/// </summary>
public sealed record AnalysisBatch
{
    /// <summary>Gets the persisted batch identifier, or zero before insertion.</summary>
    public long Id { get; init; }

    /// <summary>Gets the inventory generation analyzed by this batch.</summary>
    public long GenerationId { get; init; }

    /// <summary>Gets the report lifecycle state.</summary>
    public AnalysisBatchStatus Status { get; init; }

    /// <summary>Gets the evidence schema version.</summary>
    public int EvidenceVersion { get; init; }

    /// <summary>Gets the batch start time.</summary>
    public DateTimeOffset StartedUtc { get; init; }

    /// <summary>Gets the completion time for terminal batches.</summary>
    public DateTimeOffset? CompletedUtc { get; init; }

    /// <summary>Gets the bounded candidate-pair count.</summary>
    public long CandidateCount { get; init; }

    /// <summary>Gets the deep-comparison count.</summary>
    public long ComparisonCount { get; init; }

    /// <summary>Gets the safely reclaimable byte count.</summary>
    public long ReclaimableBytes { get; init; }

    /// <summary>Gets the compact report summary document.</summary>
    public required string SummaryJson { get; init; }

    /// <summary>Gets the terminal pair relationships.</summary>
    public IReadOnlyList<FontRelationRecord> Relations { get; init; } = [];

    /// <summary>Gets physical-file groups proven through direct replacement edges.</summary>
    public IReadOnlyList<DuplicateGroupRecord> Groups { get; init; } = [];

    /// <summary>Gets generation-bound quality scores, including report-only Faces outside replacement groups.</summary>
    public IReadOnlyList<FontFaceAnalysisRecord> FaceScores { get; init; } = [];
}
