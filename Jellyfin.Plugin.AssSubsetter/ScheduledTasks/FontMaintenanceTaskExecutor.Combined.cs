using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.ScheduledTasks;

public sealed partial class FontMaintenanceTaskExecutor
{
    /// <inheritdoc/>
    public async Task<FontTaskExecutionResult> RefreshAnalyzeAndPruneAsync(int retentionCount, IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(retentionCount);
        ArgumentNullException.ThrowIfNull(progress);
        if (!TryEnterTask(cancellationToken))
        {
            return new FontTaskExecutionResult(null, "task-active");
        }

        try
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
        finally
        {
            ReleaseTask();
        }
    }

    /// <inheritdoc/>
    public async Task<FontTaskExecutionResult> QuarantineAndPurgeAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (!TryEnterTask(cancellationToken))
        {
            return new FontTaskExecutionResult(null, "task-active");
        }

        try
        {
            FontTaskExecutionResult result = await RunQuarantineCoreAsync(new ScaledProgress(progress, 0, 90), true, cancellationToken).ConfigureAwait(false);
            if (result.Outcome is "Complete" or "no-candidates")
            {
                cancellationToken.ThrowIfCancellationRequested();
                FontTaskExecutionResult cleanup = await RunPurgeCoreAsync(new ScaledProgress(progress, 90, 99), true, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("[AssSubsetter] Quarantine cleanup completed. QuarantineRequestId: {QuarantineRequestId}; PurgeRequestId: {PurgeRequestId}; Outcome: {Outcome}.", result.OperationRequestId, cleanup.OperationRequestId, cleanup.Outcome);
            }

            return result;
        }
        finally
        {
            ReleaseTask();
        }
    }
}
