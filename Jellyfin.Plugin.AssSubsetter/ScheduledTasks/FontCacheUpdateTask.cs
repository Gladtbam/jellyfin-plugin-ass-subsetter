using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Helpers;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.ScheduledTasks;

/// <summary>Refreshes the physical font inventory and analyzes duplicates in the published generation.</summary>
public sealed class FontCacheUpdateTask : IScheduledTask
{
    private readonly IFontMaintenanceTaskExecutor _executor;
    private readonly ILogger<FontCacheUpdateTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="FontCacheUpdateTask"/> class.</summary>
    /// <param name="executor">The serialized font task executor.</param>
    /// <param name="logger">The concise scheduled-task log sink.</param>
    public FontCacheUpdateTask(
        IFontMaintenanceTaskExecutor executor,
        ILogger<FontCacheUpdateTask> logger)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Gets the localized inventory and duplicate-analysis task name.</summary>
    public string Name => LocalizationHelper.GetString("FontAnalysisTask_Name");

    /// <summary>Gets the stable Jellyfin task key retained from the original inventory task.</summary>
    public string Key => "LocalFontCacheUpdateTask";

    /// <summary>Gets the localized automatic-task description.</summary>
    public string Description => LocalizationHelper.GetString("FontAnalysisTask_Description");

    /// <summary>Gets the Jellyfin scheduled-task category.</summary>
    public string Category => "Subtitles";

    /// <summary>Refreshes inventory first and analyzes only the generation that was atomically published.</summary>
    /// <param name="progress">Receives percentage completion from zero through one hundred.</param>
    /// <param name="cancellationToken">A token that cancels refresh or analysis.</param>
    /// <returns>A task representing the scheduled invocation.</returns>
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        return FontScheduledTaskExecution.RunAsync(
            _logger,
            "inventory-analysis",
            progress,
            _executor.RefreshInventoryAndAnalyzeAsync,
            cancellationToken);
    }

    /// <summary>Gets the twenty-four-hour interval trigger without startup execution.</summary>
    /// <returns>The automatic inventory-analysis schedule.</returns>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(24).Ticks }
        ];
    }
}
