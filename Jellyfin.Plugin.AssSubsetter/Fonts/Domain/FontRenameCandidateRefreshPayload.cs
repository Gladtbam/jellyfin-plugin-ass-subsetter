namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>The immutable context of a candidate refresh.</summary>
/// <param name="GenerationId">Published inventory generation.</param>
/// <param name="AnalysisBatchId">Current source analysis.</param>
/// <param name="RuleFingerprint">Exact naming rules digest.</param>
/// <param name="CalculatorVersion">Candidate calculator compatibility.</param>
/// <param name="CalculationRequestId">The server-selected calculation root, or null for a root request.</param>
public sealed record FontRenameCandidateRefreshPayload(long GenerationId, long AnalysisBatchId, string RuleFingerprint, int CalculatorVersion, long? CalculationRequestId);
