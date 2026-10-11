using System.Collections.Generic;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Api;

/// <summary>Describes the persisted inventory, health, timings, and latest analysis summary.</summary>
/// <param name="PublishedGenerationId">The currently published inventory generation, if any.</param>
/// <param name="PublishedFileCount">The number of currently published physical font files.</param>
/// <param name="PublishedFaceCount">The number of currently published Faces.</param>
/// <param name="LatestAnalysis">The latest completed analysis, if one exists.</param>
/// <param name="EligibleQuarantineCount">The distinct physical-file count eligible for verified quarantine.</param>
/// <param name="ReclaimableBytes">The current generation analysis estimate of safely reclaimable bytes.</param>
/// <param name="DatabaseHealth">The current safe database health snapshot.</param>
/// <param name="PhaseTimings">The newest persisted inventory and analysis phase timings.</param>
public sealed record FontOverviewDto(
    long? PublishedGenerationId,
    long PublishedFileCount,
    long PublishedFaceCount,
    AnalysisBatchDto? LatestAnalysis,
    long EligibleQuarantineCount,
    long ReclaimableBytes,
    DatabaseHealthDto DatabaseHealth,
    IReadOnlyList<PerformanceSampleDto> PhaseTimings)
{
    /// <summary>Gets the latest complete scan outcome.</summary>
    public FontScanOutcomeDto? LastScan { get; init; }
}
