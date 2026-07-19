using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Helpers;
using Jellyfin.Plugin.AssSubsetter.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.ScheduledTasks;

/// <summary>
///     Scheduled task to scan physical font directories and build the local font cache index.
/// </summary>
public class FontCacheUpdateTask : IScheduledTask
{
    private readonly FontCacheManager _fontCacheManager;
    private readonly ILogger<FontCacheUpdateTask> _logger;

    /// <summary>
    ///     Initializes a new instance of the <see cref="FontCacheUpdateTask" /> class.
    /// </summary>
    /// <param name="fontCacheManager">The font cache manager.</param>
    /// <param name="logger">The logger.</param>
    public FontCacheUpdateTask(
        FontCacheManager fontCacheManager,
        ILogger<FontCacheUpdateTask> logger)
    {
        _fontCacheManager = fontCacheManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => LocalizationHelper.GetString("FontCacheTask_Name");

    /// <inheritdoc />
    public string Key => "LocalFontCacheUpdateTask";

    /// <inheritdoc />
    public string Description => LocalizationHelper.GetString("FontCacheTask_Description");

    /// <inheritdoc />
    public string Category => "Subtitles";

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        _logger.LogInformation("[AssSubsetter] Starting font database update task...");
        await _fontCacheManager.ScanAndSaveAsync(progress, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("[AssSubsetter] Font database cache build completed!");
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger },
            new TaskTriggerInfo { Type = TaskTriggerInfoType.IntervalTrigger, IntervalTicks = TimeSpan.FromHours(24).Ticks }
        ];
    }
}
