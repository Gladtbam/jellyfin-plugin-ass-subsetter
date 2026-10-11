using Jellyfin.Plugin.AssSubsetter.Fonts.Api;

namespace Jellyfin.Plugin.AssSubsetter.ScheduledTasks;

/// <summary>Summarizes one scheduled font-maintenance invocation without emitting a detailed report to the Jellyfin log.</summary>
/// <param name="BatchId">The persisted analysis or maintenance batch identifier when one was produced.</param>
/// <param name="Outcome">A compact stable outcome key.</param>
/// <param name="OperationRequestId">The persisted operation-request identifier when work was claimed.</param>
public sealed record FontTaskExecutionResult(long? BatchId, string Outcome, long? OperationRequestId = null)
{
    /// <summary>Gets the successful scan result when this invocation analyzed inventory.</summary>
    public FontScanOutcomeDto? ScanResult { get; init; }
}
