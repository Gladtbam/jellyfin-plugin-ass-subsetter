using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;
using Jellyfin.Plugin.AssSubsetter.ScheduledTasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

/// <summary>Schedules persisted font requests once Jellyfin tasks are registered and maintenance gates are idle.</summary>
public sealed class FontOperationRequestScheduler : BackgroundService, IFontOperationRequestDispatcher
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private readonly IFontOperationRepository _repository;
    private readonly ITaskManager _taskManager;
    private readonly Func<bool> _isBusy;
    private readonly Func<FontOperationKind, CancellationToken, Task<FontTaskExecutionResult>> _executePending;
    private readonly ILogger<FontOperationRequestScheduler> _logger;
    private readonly SemaphoreSlim _signal = new(1, 1);

    /// <summary>Initializes a new instance of the <see cref="FontOperationRequestScheduler"/> class.</summary>
    /// <param name="repository">The durable pending authorization queue.</param>
    /// <param name="taskManager">The registered Jellyfin task queue.</param>
    /// <param name="isBusy">The shared task gate state.</param>
    /// <param name="executePending">Consumes existing authorization without scheduled-task fallback.</param>
    /// <param name="logger">Records scheduling failures.</param>
    public FontOperationRequestScheduler(IFontOperationRepository repository, ITaskManager taskManager, Func<bool> isBusy, Func<FontOperationKind, CancellationToken, Task<FontTaskExecutionResult>> executePending, ILogger<FontOperationRequestScheduler> logger)
    {
        _repository = repository;
        _taskManager = taskManager;
        _isBusy = isBusy;
        _executePending = executePending;
        _logger = logger;
    }

    /// <summary>Signals that an existing task or maintenance gate has released.</summary>
    public void Signal()
    {
        try
        {
            _signal.Release();
        }
        catch (Exception exception) when (exception is SemaphoreFullException or ObjectDisposedException)
        {
            // A pending signal already covers the durable queue, or the host is shutting down.
        }
    }

    /// <inheritdoc/>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        _taskManager.TaskCompleted += OnTaskCompleted;
        return base.StartAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _taskManager.TaskCompleted -= OnTaskCompleted;
        return base.StopAsync(cancellationToken);
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        bool retry = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (retry)
                {
                    await _signal.WaitAsync(RetryDelay, stoppingToken).ConfigureAwait(false);
                }
                else
                {
                    await _signal.WaitAsync(stoppingToken).ConfigureAwait(false);
                }

                retry = await SchedulePendingAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                retry = true;
                _logger.LogError(exception, "Failed to schedule pending font requests.");
            }
        }
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        _taskManager.TaskCompleted -= OnTaskCompleted;
        base.Dispose();
        _signal.Dispose();
    }

    private async Task<bool> SchedulePendingAsync(CancellationToken cancellationToken)
    {
        var kinds = await _repository.LoadPendingOperationKindsAsync(cancellationToken).ConfigureAwait(false);
        bool pending = false;
        foreach (FontOperationKind kind in kinds)
        {
            Type? taskType = kind switch
            {
                FontOperationKind.Analyze or FontOperationKind.RebuildDerivedData or FontOperationKind.RefreshRenameCandidates => typeof(FontCacheUpdateTask),
                FontOperationKind.Quarantine => typeof(FontDuplicateQuarantineTask),
                FontOperationKind.Purge => typeof(FontDuplicateQuarantineTask),
                FontOperationKind.NormalizeNames or FontOperationKind.RollbackNames => typeof(FontFilenameNormalizationTask),
                _ => null
            };
            if (taskType is null)
            {
                // Restore has its own worker.
                continue;
            }

            pending = true;
            if (_isBusy())
            {
                break;
            }

            // Jellyfin registers tasks after hosted-service startup and runtime snapshot recovery.
            // Wait for registration without blocking the host's StartAsync.
            if (!_taskManager.ScheduledTasks.Any(worker => worker.ScheduledTask.GetType() == taskType))
            {
                continue;
            }

            FontTaskExecutionResult result = await _executePending(kind, cancellationToken).ConfigureAwait(false);
            if (result.OperationRequestId.HasValue)
            {
                _logger.LogInformation("Completed pending font request {RequestId} with outcome {Outcome} and batch {BatchId}.", result.OperationRequestId, result.Outcome, result.BatchId);
                Signal();
            }

            // A stale queue snapshot is safe: this entry point never synthesizes new work.
            break;
        }

        return pending;
    }

    private void OnTaskCompleted(object? sender, TaskCompletionEventArgs args) => Signal();
}
