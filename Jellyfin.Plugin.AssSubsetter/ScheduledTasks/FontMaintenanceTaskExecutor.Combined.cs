using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.ScheduledTasks;

public sealed partial class FontMaintenanceTaskExecutor
{
    /// <summary>Waits for catalog recovery, analyzes the inventory, and prunes history after a successful analysis or when the inventory is current.</summary>
    /// <param name="retentionCount">The number of analysis reports and published generations to retain. Zero retains all history.</param>
    /// <param name="progress">Receives progress across analysis and pruning.</param>
    /// <param name="cancellationToken">Cancels the recovery wait, analysis, or pruning.</param>
    /// <returns>The analysis result, or a deferred result if another maintenance task holds the execution slot.</returns>
    /// <exception cref="InvalidOperationException">The catalog could not be recovered.</exception>
    public async Task<FontTaskExecutionResult> RefreshAnalyzeAndPruneAsync(int retentionCount, IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(retentionCount);
        ArgumentNullException.ThrowIfNull(progress);
        if (!await TryEnterTaskAsync(cancellationToken).ConfigureAwait(false))
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

    /// <summary>
    /// Waits for catalog recovery, then quarantines duplicates. Applies the expiry policy when the request succeeds, already succeeded, or has no candidates.
    /// </summary>
    /// <param name="progress">Receives progress across quarantine and purge.</param>
    /// <param name="cancellationToken">Cancels the recovery wait, quarantine, or purge.</param>
    /// <returns>The quarantine result, or a deferred result if another maintenance task holds the execution slot.</returns>
    /// <exception cref="InvalidOperationException">The catalog could not be recovered.</exception>
    public async Task<FontTaskExecutionResult> QuarantineAndPurgeAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (!await TryEnterTaskAsync(cancellationToken).ConfigureAwait(false))
        {
            return new FontTaskExecutionResult(null, "task-active");
        }

        try
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
        finally
        {
            ReleaseTask();
        }
    }
}
