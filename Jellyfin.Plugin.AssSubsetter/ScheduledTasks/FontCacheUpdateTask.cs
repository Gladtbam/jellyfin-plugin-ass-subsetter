using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.Helpers;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.ScheduledTasks;

/// <summary>Runs the enabled font maintenance stages under the existing Jellyfin task identity.</summary>
public sealed class FontCacheUpdateTask : IScheduledTask
{
    private readonly IFontMaintenanceTaskExecutor _executor;
    private readonly Func<PluginConfiguration> _configuration;
    private readonly ILogger<FontCacheUpdateTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="FontCacheUpdateTask"/> class.</summary>
    /// <param name="executor">The serialized font task executor.</param>
    /// <param name="configuration">Provides the enabled stages and history retention setting.</param>
    /// <param name="logger">The concise scheduled-task log sink.</param>
    public FontCacheUpdateTask(
        IFontMaintenanceTaskExecutor executor,
        Func<PluginConfiguration> configuration,
        ILogger<FontCacheUpdateTask> logger)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Gets the localized font maintenance task name.</summary>
    public string Name => LocalizationHelper.GetString("FontMaintenanceTask_Name");

    /// <summary>Gets the stable Jellyfin task key retained from the original inventory task.</summary>
    public string Key => "LocalFontCacheUpdateTask";

    /// <summary>Gets the localized automatic-task description.</summary>
    public string Description => LocalizationHelper.GetString("FontMaintenanceTask_Description");

    /// <summary>Gets the Jellyfin scheduled-task category.</summary>
    public string Category => "Subtitles";

    /// <summary>Reads the stage switches once and runs the scheduled font maintenance workflow.</summary>
    /// <param name="progress">Receives percentage completion from zero through one hundred.</param>
    /// <param name="cancellationToken">Cancels the active stage and skips the remaining stages.</param>
    /// <returns>A task representing the scheduled invocation.</returns>
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        return FontScheduledTaskExecution.RunAsync(
            _logger,
            "font-maintenance",
            progress,
            (taskProgress, token) =>
            {
                PluginConfiguration configuration = _configuration();
                FontMaintenanceStages stages =
                    (configuration.EnableScheduledFontAnalysis ? FontMaintenanceStages.Analyze : FontMaintenanceStages.None)
                    | (configuration.EnableScheduledFontQuarantine ? FontMaintenanceStages.Quarantine : FontMaintenanceStages.None)
                    | (configuration.EnableScheduledFontRenaming ? FontMaintenanceStages.Rename : FontMaintenanceStages.None);
                return _executor.RunScheduledMaintenanceAsync(stages, configuration.FontHistoryRetentionCount, taskProgress, token);
            },
            cancellationToken);
    }

    /// <summary>Gets the twenty-four-hour interval trigger without startup execution.</summary>
    /// <returns>The automatic maintenance schedule.</returns>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(24).Ticks }
        ];
    }
}
