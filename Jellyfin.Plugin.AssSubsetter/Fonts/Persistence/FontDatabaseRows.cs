namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

// Flat rows are private to the adapter; related table definitions remain together.
internal static class FontDatabaseRows
{
    internal sealed class SchemaInfoRow
    {
        public long SingletonId { get; set; }

        public long? PublishedGenerationId { get; set; }
    }

    internal sealed class ScanGenerationRow
    {
        public long Id { get; set; }

        public int Status { get; set; }

        public string CreatedUtc { get; set; } = null!;

        public string? PublishedUtc { get; set; }

        public string InputJson { get; set; } = null!;

        public string ChangeReasonsJson { get; set; } = null!;
    }

    internal sealed class FontFileRow
    {
        public long Id { get; set; }

        public long GenerationId { get; set; }

        public string Path { get; set; } = null!;

        public string NormalizedPath { get; set; } = null!;

        public string PhysicalIdentity { get; set; } = null!;

        public long Size { get; set; }

        public string ModifiedUtc { get; set; } = null!;

        public byte[] Sha256 { get; set; } = [];

        public int DirectoryPolicy { get; set; }

        public int LinkKind { get; set; }

        public int Health { get; set; }

        public string? Error { get; set; }
    }

    internal sealed class FontFaceRow
    {
        public long Id { get; set; }

        public long FileId { get; set; }

        public int FaceIndex { get; set; }

        public byte[] StrictFingerprint { get; set; } = [];

        public string FamilyName { get; set; } = null!;

        public string StyleName { get; set; } = null!;

        public int Weight { get; set; }

        public int Width { get; set; }

        public int Slant { get; set; }

        public bool IsItalic { get; set; }

        public bool IsFixedPitch { get; set; }

        public string Technology { get; set; } = null!;

        public int UnitsPerEm { get; set; }

        public string? Version { get; set; }

        public int RetentionScore { get; set; }

        public bool IsRuntimePreferred { get; set; }
    }

    internal sealed class FontPathAliasRow
    {
        public long Id { get; set; }

        public long FileId { get; set; }

        public string Path { get; set; } = null!;

        public string NormalizedPath { get; set; } = null!;
    }

    internal sealed class FaceNameRow
    {
        public long Id { get; set; }

        public long FaceId { get; set; }

        public int Ordinal { get; set; }

        public string Name { get; set; } = null!;

        public string NormalizedName { get; set; } = null!;
    }

    internal sealed class FaceNameEvidenceRow
    {
        public long FaceId { get; set; }

        public string NamesJson { get; set; } = null!;
    }

    internal sealed class FontAxisRow
    {
        public long Id { get; set; }

        public long FaceId { get; set; }

        public int Tag { get; set; }

        public double MinimumValue { get; set; }

        public double DefaultValue { get; set; }

        public double MaximumValue { get; set; }

        public int Flags { get; set; }

        public string? Name { get; set; }
    }

    internal sealed class FontNamedInstanceRow
    {
        public long Id { get; set; }

        public long FaceId { get; set; }

        public string SubfamilyName { get; set; } = null!;

        public string CoordinatesJson { get; set; } = null!;
    }

    internal sealed class FaceTableRow
    {
        public long Id { get; set; }

        public long FaceId { get; set; }

        public int Tag { get; set; }

        public long Length { get; set; }

        public byte[] RawDigest { get; set; } = [];

        public int SemanticGroup { get; set; }

        public byte[] SemanticDigest { get; set; } = [];
    }

    internal sealed class FaceCoverageRow
    {
        public long Id { get; set; }

        public long FaceId { get; set; }

        public int CoverageKind { get; set; }

        public byte[] CompactValue { get; set; } = [];
    }

    internal sealed class FontDeepEvidenceRow
    {
        public long FileId { get; set; }

        public string EvidenceKey { get; set; } = null!;

        public byte[] PayloadGzip { get; set; } = [];
    }

    internal sealed class AnalysisBatchRow
    {
        public long Id { get; set; }

        public long GenerationId { get; set; }

        public int Status { get; set; }

        public int EvidenceVersion { get; set; }

        public string StartedUtc { get; set; } = null!;

        public string? CompletedUtc { get; set; }

        public long CandidateCount { get; set; }

        public long ComparisonCount { get; set; }

        public long ReclaimableBytes { get; set; }

        public string SummaryJson { get; set; } = null!;
    }

    internal sealed class FontRelationRow
    {
        public long Id { get; set; }

        public long AnalysisBatchId { get; set; }

        public long LeftFaceId { get; set; }

        public long RightFaceId { get; set; }

        public int RelationshipKind { get; set; }

        public int ReplacementDirection { get; set; }

        public int EvidenceVersion { get; set; }

        public string EvidenceJson { get; set; } = null!;
    }

    internal sealed class DuplicateGroupRow
    {
        public long Id { get; set; }

        public long AnalysisBatchId { get; set; }

        public long RetainedFaceId { get; set; }

        public long ReclaimableBytes { get; set; }

        public string EvidenceJson { get; set; } = null!;
    }

    internal sealed class GroupMemberRow
    {
        public long Id { get; set; }

        public long DuplicateGroupId { get; set; }

        public long FaceId { get; set; }

        public long FileId { get; set; }

        public int Decision { get; set; }

        public int RetentionScore { get; set; }

        public string ScoreJson { get; set; } = null!;

        public string Reason { get; set; } = null!;
    }

    internal sealed class OperationRequestRow
    {
        public long Id { get; set; }

        public int OperationKind { get; set; }

        public int Status { get; set; }

        public long? AnalysisBatchId { get; set; }

        public string IdempotencyKey { get; set; } = null!;

        public string RequestJson { get; set; } = null!;

        public string CreatedUtc { get; set; } = null!;

        public string? ClaimedUtc { get; set; }

        public string? CompletedUtc { get; set; }

        public string? Error { get; set; }

        public string? ResultJson { get; set; }
    }

    internal sealed class MaintenanceBatchRow
    {
        public long Id { get; set; }

        public long OperationRequestId { get; set; }

        public int OperationKind { get; set; }

        public int Status { get; set; }

        public string StartedUtc { get; set; } = null!;

        public string? CompletedUtc { get; set; }

        public string SummaryJson { get; set; } = null!;
    }

    internal sealed class QuarantineItemRow
    {
        public long Id { get; set; }

        public long MaintenanceBatchId { get; set; }

        public long FileId { get; set; }

        public string SourcePath { get; set; } = null!;

        public string StagingPath { get; set; } = null!;

        public string QuarantinePath { get; set; } = null!;

        public byte[] Sha256 { get; set; } = [];

        public long Size { get; set; }

        public int Status { get; set; }

        public string? QuarantinedUtc { get; set; }

        public string? ExpiresUtc { get; set; }

        public string? RestoredUtc { get; set; }

        public string? Error { get; set; }
    }

    internal sealed class RenameItemRow
    {
        public long Id { get; set; }

        public long MaintenanceBatchId { get; set; }

        public long FileId { get; set; }

        public string OldPath { get; set; } = null!;

        public string NewPath { get; set; } = null!;

        public byte[] Sha256 { get; set; } = [];

        public int Status { get; set; }

        public string? RenamedUtc { get; set; }

        public string? RolledBackUtc { get; set; }

        public string? Error { get; set; }

        public string ItemJson { get; set; } = null!;

        public long GenerationId { get; set; }

        public string PhysicalIdentity { get; set; } = null!;

        public string SourcePathKey { get; set; } = null!;

        public string TargetPathKey { get; set; } = null!;

        public string SourceKey { get; set; } = null!;

        public string Category { get; set; } = null!;
    }

    internal sealed class RenameCandidateStateRow
    {
        public long SingletonId { get; set; }

        public long GenerationId { get; set; }

        public long AnalysisBatchId { get; set; }

        public string RuleFingerprint { get; set; } = null!;

        public int Status { get; set; }

        public long? OperationRequestId { get; set; }

        public string StateJson { get; set; } = null!;
    }

    internal sealed class RenameCandidateRow
    {
        public long FileId { get; set; }

        public long GenerationId { get; set; }

        public string PhysicalIdentity { get; set; } = null!;

        public string SourcePathKey { get; set; } = null!;

        public string TargetPathKey { get; set; } = null!;

        public string SourceKey { get; set; } = null!;

        public string OldPath { get; set; } = null!;

        public string NewPath { get; set; } = null!;

        public string Category { get; set; } = null!;

        public string? SkipReason { get; set; }

        public string ItemJson { get; set; } = null!;
    }

    internal sealed class RenameRequestItemRow
    {
        public long Id { get; set; }

        public long RequestId { get; set; }

        public long FileId { get; set; }

        public long GenerationId { get; set; }

        public string PhysicalIdentity { get; set; } = null!;

        public string SourceKey { get; set; } = null!;

        public string OldPath { get; set; } = null!;

        public string NewPath { get; set; } = null!;

        public string SourcePathKey { get; set; } = null!;

        public string TargetPathKey { get; set; } = null!;

        public string ItemJson { get; set; } = null!;
    }

    internal sealed class PerformanceSampleRow
    {
        public long Id { get; set; }

        public long? GenerationId { get; set; }

        public string Phase { get; set; } = null!;

        public long DurationTicks { get; set; }

        public long ItemCount { get; set; }

        public long ReusedCount { get; set; }

        public string RecordedUtc { get; set; } = null!;
    }
}
