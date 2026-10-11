using System;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>Immutable FontRenameCandidateState data for current rename candidates.</summary>
/// <param name="GenerationId">The GenerationId value.</param>
/// <param name="AnalysisBatchId">The AnalysisBatchId value.</param>
/// <param name="Rules">The Rules value.</param>
/// <param name="RuleFingerprint">The RuleFingerprint value.</param>
/// <param name="CalculatorVersion">The CalculatorVersion value.</param>
/// <param name="Status">The Status value.</param>
/// <param name="GeneratedUtc">The GeneratedUtc value.</param>
/// <param name="OperationRequestId">The OperationRequestId value.</param>
/// <param name="Error">The Error value.</param>
public sealed record FontRenameCandidateState(long GenerationId, long AnalysisBatchId, FontNamingRules Rules, string RuleFingerprint, int CalculatorVersion, FontRenameCandidateGenerationStatus Status, DateTimeOffset? GeneratedUtc, long? OperationRequestId, string? Error);
