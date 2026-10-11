using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.ScheduledTasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

/// <summary>Executes durable restore requests without exposing a Jellyfin scheduled task.</summary>
public sealed class FontRestoreRequestWorker : BackgroundService, IFontRestoreRequestDispatcher
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private readonly IFontMaintenanceTaskExecutor _executor;
    private readonly ILogger<FontRestoreRequestWorker> _logger;
    private readonly SemaphoreSlim _signal = new(0, 1);

    /// <summary>Initializes a new instance of the <see cref="FontRestoreRequestWorker"/> class.</summary>
    /// <param name="executor">Executes durable restore requests.</param>
    /// <param name="logger">Records background failures.</param>
    public FontRestoreRequestWorker(IFontMaintenanceTaskExecutor executor, ILogger<FontRestoreRequestWorker> logger)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public void Signal()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // One pending wake-up already covers every durable request in the queue.
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Also drain requests persisted before startup or between persistence and the previous shutdown.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                FontTaskExecutionResult result = await _executor.RestoreQuarantinedFontsAsync(stoppingToken).ConfigureAwait(false);
                if (result.Outcome == "request-missing")
                {
                    await _signal.WaitAsync(stoppingToken).ConfigureAwait(false);
                }
                else if (result.Outcome == "task-active")
                {
                    await _signal.WaitAsync(RetryDelay, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to process a quarantined-font restore request.");
                await _signal.WaitAsync(RetryDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        base.Dispose();
        _signal.Dispose();
    }
}
