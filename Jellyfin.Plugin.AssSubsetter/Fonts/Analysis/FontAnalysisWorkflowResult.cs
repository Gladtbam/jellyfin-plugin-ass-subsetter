using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;

/// <summary>The analysis result before the caller records the operation's terminal state.</summary>
/// <param name="Batch">The published, reused or incomplete batch.</param>
/// <param name="Scan">The inventory scan bound to the batch.</param>
public sealed record FontAnalysisWorkflowResult(AnalysisBatch Batch, FontInventoryScanResult Scan);
