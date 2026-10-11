namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>Immutable FontRenameCandidateReadRequirements data for current rename candidates.</summary>
/// <param name="RuleFingerprint">The RuleFingerprint value.</param>
/// <param name="PolicyFingerprint">The PolicyFingerprint value.</param>
/// <param name="EvidenceFingerprint">The EvidenceFingerprint value.</param>
/// <param name="AnalysisEvidenceVersion">The AnalysisEvidenceVersion value.</param>
/// <param name="CalculatorVersion">The CalculatorVersion value.</param>
public sealed record FontRenameCandidateReadRequirements(string RuleFingerprint, string PolicyFingerprint, string EvidenceFingerprint, int AnalysisEvidenceVersion, int CalculatorVersion);
