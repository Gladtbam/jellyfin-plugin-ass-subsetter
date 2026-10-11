using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>Immutable FontPublishedAnalysisContext data for current rename candidates.</summary>
/// <param name="GenerationId">The GenerationId value.</param>
/// <param name="AnalysisBatchId">The AnalysisBatchId value.</param>
/// <param name="EvidenceVersion">The EvidenceVersion value.</param>
/// <param name="Input">The Input value.</param>
public sealed record FontPublishedAnalysisContext(long GenerationId, long AnalysisBatchId, int EvidenceVersion, FontAnalysisInputSnapshot Input);
