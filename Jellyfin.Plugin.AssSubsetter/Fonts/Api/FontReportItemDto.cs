using System.Collections.Generic;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Api;

/// <summary>Describes one relationship or unclassifiable-file analysis report row.</summary>
/// <param name="AnalysisBatchId">The persisted analysis batch identifier.</param>
/// <param name="RelationId">The persisted relationship identifier, if this is a pair row.</param>
/// <param name="RelationshipKind">The relationship classification, if this is a pair row.</param>
/// <param name="IsUnclassifiable">Whether this row represents unclassifiable persisted evidence.</param>
/// <param name="Action">The safe administrator action category.</param>
/// <param name="FileIds">The related persisted physical-file identifiers.</param>
/// <param name="FaceIds">The related persisted Face identifiers.</param>
/// <param name="Paths">The related physical paths rendered as text.</param>
/// <param name="Directories">The related parent directories rendered as text.</param>
/// <param name="Technologies">The related allowlisted font technologies.</param>
/// <param name="Diagnostic">The persisted unclassifiable diagnostic, if present.</param>
/// <param name="PairEvidence">The structured pair detail, including report-only relationships.</param>
/// <param name="Scores">The safe five-component Face scores and quality verification outcomes.</param>
/// <param name="RetainedRationale">The persisted safe retention rationale.</param>
public sealed record FontReportItemDto(
    long AnalysisBatchId,
    long? RelationId,
    FontRelationshipKind? RelationshipKind,
    bool IsUnclassifiable,
    FontReportAction Action,
    IReadOnlyList<long> FileIds,
    IReadOnlyList<long> FaceIds,
    IReadOnlyList<string> Paths,
    IReadOnlyList<string> Directories,
    IReadOnlyList<string> Technologies,
    string? Diagnostic,
    PairEvidenceDto? PairEvidence,
    IReadOnlyList<FontReportScoreDto> Scores,
    IReadOnlyList<string> RetainedRationale)
{
    /// <summary>Gets the verified whole-file plan represented by this row, when present.</summary>
    public FontReportReplacementDto? Replacement { get; init; }
}
