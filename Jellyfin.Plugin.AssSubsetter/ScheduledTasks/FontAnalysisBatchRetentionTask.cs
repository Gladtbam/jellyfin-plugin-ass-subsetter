using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.Helpers;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.ScheduledTasks;

/// <summary>Prunes old analysis reports, filename previews, and unused font inventory generations.</summary>
public sealed class FontAnalysisBatchRetentionTask : IScheduledTask
{
    private readonly IFontMaintenanceTaskExecutor _executor;
    private readonly Func<PluginConfiguration> _configuration;
    private readonly ILogger<FontAnalysisBatchRetentionTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="FontAnalysisBatchRetentionTask"/> class.</summary>
    /// <param name="executor">The serialized font maintenance coordinator.</param>
    /// <param name="configuration">The current plugin configuration provider.</param>
    /// <param name="logger">The task log sink.</param>
    public FontAnalysisBatchRetentionTask(
        IFontMaintenanceTaskExecutor executor,
        Func<PluginConfiguration> configuration,
        ILogger<FontAnalysisBatchRetentionTask> logger)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Gets the localized task name.</summary>
    public string Name => LocalizationHelper.GetString("FontAnalysisRetentionTask_Name");

    /// <summary>Gets the stable scheduled task key.</summary>
    public string Key => "FontAnalysisBatchRetentionTask";

    /// <summary>Gets the localized task description.</summary>
    public string Description => LocalizationHelper.GetString("FontAnalysisRetentionTask_Description");

    /// <summary>Gets the Jellyfin task category.</summary>
    public string Category => "Subtitles";

    /// <summary>Applies the shared history retention setting and removes obsolete filename previews.</summary>
    /// <param name="progress">Receives task progress.</param>
    /// <param name="cancellationToken">Cancels pruning.</param>
    /// <returns>A task representing the cleanup.</returns>
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        return FontScheduledTaskExecution.RunAsync(
            _logger,
            "history-prune",
            progress,
            async (taskProgress, token) =>
            {
                int retentionCount = _configuration().FontHistoryRetentionCount;
                if (retentionCount < 0)
                {
                    throw new InvalidOperationException("History retention count cannot be negative.");
                }

                FontHistoryPruneResult result = await _executor.PruneHistoryAsync(retentionCount, taskProgress, token).ConfigureAwait(false);
                if (result.Skipped)
                {
                    return new FontTaskExecutionResult(null, "task-active");
                }

                _logger.LogInformation(
                    "[AssSubsetter] History pruning completed. RetentionCount: {RetentionCount}; DeletedBatches: {DeletedBatches}; DeletedGenerations: {DeletedGenerations}.",
                    retentionCount,
                    result.DeletedBatches,
                    result.DeletedGenerations);
                return new FontTaskExecutionResult(null, "pruned");
            },
            cancellationToken);
    }

    /// <summary>Runs once every twenty-four hours without a startup trigger.</summary>
    /// <returns>The automatic cleanup schedule.</returns>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(24).Ticks }
        ];
    }
}
