using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Helpers;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.ScheduledTasks;

/// <summary>Purges expired quarantine copies after fixed age, hash, and replacement verification.</summary>
public sealed class FontQuarantinePurgeTask : IScheduledTask
{
    private readonly IFontMaintenanceTaskExecutor _executor;
    private readonly ILogger<FontQuarantinePurgeTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="FontQuarantinePurgeTask"/> class.</summary>
    /// <param name="executor">The serialized font task executor.</param>
    /// <param name="logger">The concise scheduled-task log sink.</param>
    public FontQuarantinePurgeTask(
        IFontMaintenanceTaskExecutor executor,
        ILogger<FontQuarantinePurgeTask> logger)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Gets the localized quarantine-purge task name.</summary>
    public string Name => LocalizationHelper.GetString("FontPurgeTask_Name");

    /// <summary>Gets the stable Jellyfin task key.</summary>
    public string Key => "FontQuarantinePurgeTask";

    /// <summary>Gets the localized automatic-task description.</summary>
    public string Description => LocalizationHelper.GetString("FontPurgeTask_Description");

    /// <summary>Gets the Jellyfin scheduled-task category.</summary>
    public string Category => "Subtitles";

    /// <summary>Delegates expiry enforcement to the fixed thirty-day quarantine policy.</summary>
    /// <param name="progress">Receives percentage completion from zero through one hundred.</param>
    /// <param name="cancellationToken">A token that cancels before the next purge item.</param>
    /// <returns>A task representing the scheduled invocation.</returns>
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        return FontScheduledTaskExecution.RunAsync(
            _logger,
            "quarantine-purge",
            progress,
            _executor.PurgeExpiredQuarantineAsync,
            cancellationToken);
    }

    /// <summary>Gets the automatic twenty-four-hour interval trigger without startup execution.</summary>
    /// <returns>The approved automatic quarantine-purge schedule.</returns>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(24).Ticks }
        ];
    }
}
