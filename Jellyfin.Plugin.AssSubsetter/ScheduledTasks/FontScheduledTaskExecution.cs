using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.ScheduledTasks;

internal static class FontScheduledTaskExecution
{
    internal const string BatchIdDataKey = "AssSubsetter.FontBatchId";
    internal const string OperationRequestIdDataKey = "AssSubsetter.FontOperationRequestId";

    public static async Task RunAsync(
        ILogger logger,
        string operation,
        IProgress<double> progress,
        Func<IProgress<double>, CancellationToken, Task<FontTaskExecutionResult>> execute,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(execute);
        var taskProgress = new ScheduledProgress(progress);
        taskProgress.Report(0);
        var stopwatch = Stopwatch.StartNew();
        logger.LogInformation("[AssSubsetter] Font maintenance {Operation} started.", operation);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            FontTaskExecutionResult result = await execute(taskProgress, cancellationToken).ConfigureAwait(false);
            if (result.Outcome == "task-active")
            {
                throw new OperationCanceledException("Another font maintenance task is active; this scheduled execution was cancelled.");
            }

            if (result.Outcome is "Cancelled" or "Failed" or "Running" or "Stale")
            {
                Exception exception = result.Outcome == "Cancelled"
                    ? new OperationCanceledException("The font maintenance batch was cancelled.")
                    : new InvalidOperationException("The font maintenance batch did not complete: " + result.Outcome);
                if (result.OperationRequestId.HasValue)
                {
                    exception.Data[OperationRequestIdDataKey] = result.OperationRequestId.Value;
                }

                if (result.BatchId.HasValue)
                {
                    exception.Data[BatchIdDataKey] = result.BatchId.Value;
                }

                throw exception;
            }

            taskProgress.Complete();
            logger.LogInformation(
                "[AssSubsetter] Font maintenance {Operation} completed. RequestId: {RequestId}; BatchId: {BatchId}; Outcome: {Outcome}; ElapsedMs: {ElapsedMs}.",
                operation,
                result.OperationRequestId,
                result.BatchId,
                result.Outcome,
                stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException exception)
        {
            logger.LogInformation(
                "[AssSubsetter] Font maintenance {Operation} cancelled. RequestId: {RequestId}; BatchId: {BatchId}; Reason: {Reason}; ElapsedMs: {ElapsedMs}.",
                operation,
                GetOperationRequestId(exception),
                GetBatchId(exception),
                exception.Message,
                stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "[AssSubsetter] Font maintenance {Operation} failed. RequestId: {RequestId}; BatchId: {BatchId}; ElapsedMs: {ElapsedMs}.",
                operation,
                GetOperationRequestId(exception),
                GetBatchId(exception),
                stopwatch.ElapsedMilliseconds);
            throw;
        }
        finally
        {
            taskProgress.Stop();
        }
    }

    private static long? GetBatchId(Exception exception)
        => exception.Data[BatchIdDataKey] is long batchId ? batchId : null;

    private static long? GetOperationRequestId(Exception exception)
        => exception.Data[OperationRequestIdDataKey] is long requestId ? requestId : null;

    // Ignore delayed callbacks and reserve 100 for successful task finalization.
    private sealed class ScheduledProgress(IProgress<double> target) : IProgress<double>
    {
        private readonly object _sync = new();
        private double _last = -1;
        private bool _complete;

        public void Report(double value)
        {
            if (!double.IsFinite(value))
            {
                return;
            }

            lock (_sync)
            {
                double bounded = Math.Clamp(value, 0, 99);
                if (_complete || bounded <= _last || (bounded < 99 && bounded - _last < 0.1))
                {
                    return;
                }

                _last = bounded;
                target.Report(bounded);
            }
        }

        public void Stop()
        {
            lock (_sync)
            {
                _complete = true;
            }
        }

        public void Complete()
        {
            lock (_sync)
            {
                _complete = true;
                target.Report(100);
            }
        }
    }
}
