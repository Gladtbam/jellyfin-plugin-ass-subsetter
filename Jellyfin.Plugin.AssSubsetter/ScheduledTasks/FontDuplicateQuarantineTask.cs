using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Helpers;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.ScheduledTasks;

/// <summary>Quarantines verified duplicate Font Files from the latest current complete analysis.</summary>
public sealed class FontDuplicateQuarantineTask : IScheduledTask
{
    private readonly IFontMaintenanceTaskExecutor _executor;
    private readonly ILogger<FontDuplicateQuarantineTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="FontDuplicateQuarantineTask"/> class.</summary>
    /// <param name="executor">The serialized font task executor.</param>
    /// <param name="logger">The concise scheduled-task log sink.</param>
    public FontDuplicateQuarantineTask(
        IFontMaintenanceTaskExecutor executor,
        ILogger<FontDuplicateQuarantineTask> logger)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Gets the localized duplicate-quarantine task name.</summary>
    public string Name => LocalizationHelper.GetString("FontQuarantineTask_Name");

    /// <summary>Gets the stable Jellyfin task key.</summary>
    public string Key => "FontDuplicateQuarantineTask";

    /// <summary>Gets the localized automatic-task description.</summary>
    public string Description => LocalizationHelper.GetString("FontQuarantineTask_Description");

    /// <summary>Gets the Jellyfin scheduled-task category.</summary>
    public string Category => "Subtitles";

    /// <summary>Consumes one explicit request or one idempotent automatic request.</summary>
    /// <param name="progress">Receives percentage completion from zero through one hundred.</param>
    /// <param name="cancellationToken">A token that cancels before the next safe file transition.</param>
    /// <returns>A task representing the scheduled invocation.</returns>
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        return FontScheduledTaskExecution.RunAsync(
            _logger,
            "duplicate-quarantine",
            progress,
            _executor.QuarantineAndPurgeAsync,
            cancellationToken);
    }

    /// <summary>Gets no default triggers; administrators can run or schedule quarantine explicitly.</summary>
    /// <returns>An empty trigger sequence.</returns>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
}
