using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Helpers;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.ScheduledTasks;

/// <summary>Normalizes eligible font filenames only after explicit administrator authorization.</summary>
public sealed class FontFilenameNormalizationTask : IScheduledTask
{
    private readonly IFontMaintenanceTaskExecutor _executor;
    private readonly ILogger<FontFilenameNormalizationTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="FontFilenameNormalizationTask"/> class.</summary>
    /// <param name="executor">The serialized font task executor.</param>
    /// <param name="logger">The concise scheduled-task log sink.</param>
    public FontFilenameNormalizationTask(
        IFontMaintenanceTaskExecutor executor,
        ILogger<FontFilenameNormalizationTask> logger)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Gets the localized filename-normalization task name.</summary>
    public string Name => LocalizationHelper.GetString("FontNormalizationTask_Name");

    /// <summary>Gets the stable Jellyfin task key.</summary>
    public string Key => "FontFilenameNormalizationTask";

    /// <summary>Gets the localized opt-in task description.</summary>
    public string Description => LocalizationHelper.GetString("FontNormalizationTask_Description");

    /// <summary>Gets the Jellyfin scheduled-task category.</summary>
    public string Category => "Subtitles";

    /// <summary>Claims and executes at most one explicit normalization request.</summary>
    /// <param name="progress">Receives percentage completion from zero through one hundred.</param>
    /// <param name="cancellationToken">A token that cancels before the next safe rename transition.</param>
    /// <returns>A task representing the scheduled invocation.</returns>
    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        return FontScheduledTaskExecution.RunAsync(
            _logger,
            "filename-normalization",
            progress,
            _executor.NormalizeFilenamesAsync,
            cancellationToken);
    }

    /// <summary>Gets no default triggers because any schedule is explicit administrator authorization.</summary>
    /// <returns>An empty trigger sequence.</returns>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
}
