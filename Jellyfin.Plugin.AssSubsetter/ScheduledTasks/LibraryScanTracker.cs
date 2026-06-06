using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.ScheduledTasks;

/// <summary>
/// Background service that tracks library scans and preemptively generates subset fonts.
/// </summary>
public class LibraryScanTracker : IHostedService, IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly SubtitleCacheService _cacheService;
    private readonly PluginConfiguration _config;
    private readonly ILogger<LibraryScanTracker> _logger;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="LibraryScanTracker"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager instance.</param>
    /// <param name="cacheService">The subtitle cache service.</param>
    /// <param name="config">The plugin configuration instance.</param>
    /// <param name="logger">The logger instance.</param>
    public LibraryScanTracker(
        ILibraryManager libraryManager,
        SubtitleCacheService cacheService,
        PluginConfiguration config,
        ILogger<LibraryScanTracker> logger)
    {
        _libraryManager = libraryManager;
        _cacheService = cacheService;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        _libraryManager.ItemUpdated += OnItemUpdated;
        _logger.LogInformation("[AssSubsetter] LibraryScanTracker started.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_libraryManager != null)
            {
                _libraryManager.ItemAdded -= OnItemAdded;
                _libraryManager.ItemUpdated -= OnItemUpdated;
            }
        }
        catch (Exception)
        {
            // 忽略卸载时的任何静默异常
        }

        return Task.CompletedTask;
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e) => ProcessItem(e.Item);

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e) => ProcessItem(e.Item);

    private void ProcessItem(BaseItem item)
    {
        if (!_config.EnableAutoScanProcessing)
        {
            return;
        }

        if (item is Video video)
        {
            Task.Run(async () =>
            {
                try
                {
                    string originalAssPath = GetOriginalAssPath(video);
                    if (!string.IsNullOrEmpty(originalAssPath))
                    {
                        _logger.LogInformation("[AssSubsetter] Auto-scan generating subset font for newly added/updated item: {ItemId}", video.Id);
                        await _cacheService.GetOrGenerateSubtitleAsync(video.Id, originalAssPath, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[AssSubsetter] Error auto-generating subtitle for item {ItemId}", video.Id);
                }
            });
        }
    }

    private static string GetOriginalAssPath(Video video)
    {
        if (string.IsNullOrEmpty(video.Path))
        {
            return string.Empty;
        }

        string videoDir = Path.GetDirectoryName(video.Path) ?? string.Empty;
        string videoNameWithoutExt = Path.GetFileNameWithoutExtension(video.Path);
        string exactMatch = Path.Combine(videoDir, videoNameWithoutExt + ".ass");
        if (File.Exists(exactMatch))
        {
            return exactMatch;
        }

        try
        {
            if (Directory.Exists(videoDir))
            {
                var assFiles = Directory.GetFiles(videoDir, videoNameWithoutExt + "*.ass")
                    .Where(f => !f.Contains("subsetted", StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                if (assFiles.Length > 0)
                {
                    return assFiles[0];
                }
            }
        }
        catch
        {
             /* Ignore */
        }

        return string.Empty;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases unmanaged and - optionally - managed resources.
    /// </summary>
    /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            try
            {
                if (_libraryManager != null)
                {
                    _libraryManager.ItemAdded -= OnItemAdded;
                    _libraryManager.ItemUpdated -= OnItemUpdated;
                }
            }
            catch (Exception)
            {
                // 忽略销毁时的任何依赖异常
            }
        }

        _disposed = true;
    }
}
