using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.ScheduledTasks;

public sealed partial class FontMaintenanceTaskExecutor
{
    /// <summary>Runs enabled inventory, quarantine, and rename stages in order under one shared execution slot.</summary>
    /// <param name="stages">The stages selected in plugin settings when the task starts.</param>
    /// <param name="retentionCount">The number of analysis reports and generations to retain. Zero retains all history.</param>
    /// <param name="progress">Receives progress divided among the enabled stages.</param>
    /// <param name="cancellationToken">Cancels the recovery wait or stops the run before subsequent stages.</param>
    /// <returns>The last stage result or an outcome indicating why no stages ran.</returns>
    /// <exception cref="InvalidOperationException">The catalog could not be recovered.</exception>
    public async Task<FontTaskExecutionResult> RunScheduledMaintenanceAsync(
        FontMaintenanceStages stages,
        int retentionCount,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        cancellationToken.ThrowIfCancellationRequested();

        var steps = new List<(FontMaintenanceStages Stage, Func<IProgress<double>, CancellationToken, Task<FontTaskExecutionResult>> Execute)>(3);
        if (stages.HasFlag(FontMaintenanceStages.Analyze))
        {
            ArgumentOutOfRangeException.ThrowIfNegative(retentionCount);
            steps.Add((FontMaintenanceStages.Analyze, (stageProgress, token) => RunAnalysisAndPruneCoreAsync(retentionCount, stageProgress, token)));
        }

        if (stages.HasFlag(FontMaintenanceStages.Quarantine))
        {
            steps.Add((FontMaintenanceStages.Quarantine, RunQuarantineAndPurgeCoreAsync));
        }

        if (stages.HasFlag(FontMaintenanceStages.Rename))
        {
            steps.Add((FontMaintenanceStages.Rename, (stageProgress, token) => RunFilenameOperationCoreAsync(stageProgress, allowScheduledWork: true, token)));
        }

        FontTaskExecutionResult result = new(null, "all-stages-disabled");
        if (steps.Count == 0)
        {
            return result;
        }

        if (!await TryEnterTaskAsync(cancellationToken).ConfigureAwait(false))
        {
            return new FontTaskExecutionResult(null, "task-active");
        }

        try
        {
            for (int index = 0; index < steps.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (stage, execute) = steps[index];
                double start = 100d * index / steps.Count;
                double end = 100d * (index + 1) / steps.Count;
                result = await execute(new ScaledProgress(progress, start, end), cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "[AssSubsetter] Font maintenance stage {Stage} ended. RequestId: {RequestId}. BatchId: {BatchId}. Outcome: {Outcome}.",
                    stage,
                    result.OperationRequestId,
                    result.BatchId,
                    result.Outcome);
                if (FontScheduledTaskExecution.IsIncompleteOutcome(result.Outcome))
                {
                    return result;
                }

                progress.Report(end);
            }

            return result;
        }
        finally
        {
            ReleaseTask();
        }
    }

    /// <summary>Analyzes the inventory and prunes history under one shared execution slot.</summary>
    /// <param name="retentionCount">The number of analysis reports and published generations to retain. Zero retains all history.</param>
    /// <param name="progress">Receives progress across analysis and pruning.</param>
    /// <param name="cancellationToken">Cancels the recovery wait, analysis, or pruning.</param>
    /// <returns>The analysis result or a deferred result if the execution slot is occupied.</returns>
    public Task<FontTaskExecutionResult> RefreshAnalyzeAndPruneAsync(int retentionCount, IProgress<double> progress, CancellationToken cancellationToken)
        => RunScheduledMaintenanceAsync(FontMaintenanceStages.Analyze, retentionCount, progress, cancellationToken);

    /// <summary>Quarantines duplicates and applies the expiry policy after successful quarantine or when there are no candidates.</summary>
    /// <param name="progress">Receives progress across quarantine and purge.</param>
    /// <param name="cancellationToken">Cancels the recovery wait, quarantine, or purge.</param>
    /// <returns>The quarantine result or a deferred result if the execution slot is occupied.</returns>
    public Task<FontTaskExecutionResult> QuarantineAndPurgeAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => RunScheduledMaintenanceAsync(FontMaintenanceStages.Quarantine, retentionCount: 0, progress, cancellationToken);

    private async Task<FontTaskExecutionResult> RunAnalysisAndPruneCoreAsync(int retentionCount, IProgress<double> progress, CancellationToken cancellationToken)
    {
        FontTaskExecutionResult result = await RunAnalysisCoreAsync(new ScaledProgress(progress, 0, 90), true, cancellationToken).ConfigureAwait(false);
        if (result.Outcome is "analyzed" or "no-changes")
        {
            cancellationToken.ThrowIfCancellationRequested();
            FontHistoryPruneResult removed = await PruneHistoryCoreAsync(retentionCount, new ScaledProgress(progress, 90, 99), cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("[AssSubsetter] Analysis history pruning completed. RequestId: {RequestId}; DeletedBatches: {DeletedBatches}; DeletedGenerations: {DeletedGenerations}.", result.OperationRequestId, removed.DeletedBatches, removed.DeletedGenerations);
        }

        return result;
    }

    private async Task<FontTaskExecutionResult> RunQuarantineAndPurgeCoreAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        FontTaskExecutionResult result = await RunQuarantineCoreAsync(new ScaledProgress(progress, 0, 90), true, cancellationToken).ConfigureAwait(false);
        bool completedEarlier = result.Outcome == "request-already-processed"
            && result.OperationRequestId is long requestId
            && (await _repository.GetOperationRequestAsync(requestId, cancellationToken).ConfigureAwait(false))?.Status == OperationRequestStatus.Complete
            && (await _repository.GetMaintenanceBatchByOperationRequestAsync(requestId, cancellationToken).ConfigureAwait(false))?.Status == MaintenanceBatchStatus.Complete;
        if (result.Outcome is "Complete" or "no-candidates" || completedEarlier)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FontTaskExecutionResult cleanup = await RunPurgeCoreAsync(new ScaledProgress(progress, 90, 99), true, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("[AssSubsetter] Quarantine cleanup completed. QuarantineRequestId: {QuarantineRequestId}; PurgeRequestId: {PurgeRequestId}; Outcome: {Outcome}.", result.OperationRequestId, cleanup.OperationRequestId, cleanup.Outcome);
        }

        return result;
    }
}
