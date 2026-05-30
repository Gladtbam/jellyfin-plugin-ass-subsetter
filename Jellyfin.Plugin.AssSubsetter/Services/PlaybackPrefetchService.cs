using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AssSubsetter.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.Services;

/// <summary>
/// Background service that monitors playback progress and prefetches (subsets)
/// the next episode's ASS subtitles when the user is nearing the end of the current episode.
/// </summary>
public class PlaybackPrefetchService : IHostedService, IDisposable
{
    private readonly ISessionManager _sessionManager;
    private readonly ILibraryManager _libraryManager;
    private readonly SubtitleCacheService _cacheService;
    private readonly PluginConfiguration _config;
    private readonly ILogger<PlaybackPrefetchService> _logger;

    /// <summary>
    /// Tracks which session+item combinations have already triggered a prefetch
    /// to avoid redundant work. Key format: "{SessionId}_{ItemId}".
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _triggeredPrefetches = new();

    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="PlaybackPrefetchService"/> class.
    /// </summary>
    /// <param name="sessionManager">The session manager instance.</param>
    /// <param name="libraryManager">The library manager instance.</param>
    /// <param name="cacheService">The subtitle cache service.</param>
    /// <param name="config">The plugin configuration instance.</param>
    /// <param name="logger">The logger instance.</param>
    public PlaybackPrefetchService(
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        SubtitleCacheService cacheService,
        PluginConfiguration config,
        ILogger<PlaybackPrefetchService> logger)
    {
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _cacheService = cacheService;
        _config = config;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _logger.LogInformation("AssSubsetter PlaybackPrefetchService started.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _sessionManager.PlaybackProgress -= OnPlaybackProgress;
            _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        }
        catch (Exception)
        {
            // 忽略卸载时的任何静默异常
        }

        return Task.CompletedTask;
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        if (!_config.EnablePrefetchSubsetting)
        {
            return;
        }

        // 仅处理剧集类型
        if (e.Item is not Episode episode)
        {
            return;
        }

        // 需要有播放位置和总时长信息
        long? positionTicks = e.PlaybackPositionTicks;
        long? runtimeTicks = episode.RunTimeTicks;

        if (positionTicks is null or <= 0 || runtimeTicks is null or <= 0)
        {
            return;
        }

        double progressPercent = (double)positionTicks.Value / runtimeTicks.Value * 100.0;

        if (progressPercent < _config.PrefetchTriggerPercent)
        {
            return;
        }

        // 构建防重复 key
        string sessionId = e.Session?.Id ?? string.Empty;
        string prefetchKey = $"{sessionId}_{episode.Id}";

        // 使用 TryAdd 保证同一次会话中只触发一次
        if (!_triggeredPrefetches.TryAdd(prefetchKey, 0))
        {
            return;
        }

        _logger.LogInformation(
            "Playback progress for episode {EpisodeName} (S{Season}E{Episode}) reached {Percent:F1}%, triggering prefetch for next episode.",
            episode.Name,
            episode.ParentIndexNumber,
            episode.IndexNumber,
            progressPercent);

        // 在后台线程执行预取，不阻塞进度上报
        _ = Task.Run(async () =>
        {
            try
            {
                await PrefetchNextEpisodeAsync(episode).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred while prefetching next episode subtitles.");
            }
        });
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        if (e.Item is not Episode episode)
        {
            return;
        }

        string sessionId = e.Session?.Id ?? string.Empty;
        string prefetchKey = $"{sessionId}_{episode.Id}";

        _triggeredPrefetches.TryRemove(prefetchKey, out _);
    }

    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Paths are determined solely from trusted database objects.")]
    private async Task PrefetchNextEpisodeAsync(Episode currentEpisode)
    {
        if (currentEpisode.IndexNumber is null)
        {
            _logger.LogDebug("Current episode has no index number, skipping prefetch.");
            return;
        }

        var nextEpisode = FindNextEpisode(currentEpisode);

        if (nextEpisode is null)
        {
            _logger.LogDebug("No next episode found for {EpisodeName}, skipping prefetch.", currentEpisode.Name);
            return;
        }

        _logger.LogInformation(
            "Found next episode: {NextEpisodeName} (S{Season}E{Episode}). Prefetching all ASS subtitles...",
            nextEpisode.Name,
            nextEpisode.ParentIndexNumber,
            nextEpisode.IndexNumber);

        string[] assPaths = GetAllOriginalAssPaths(nextEpisode);

        if (assPaths.Length == 0)
        {
            _logger.LogDebug("No external ASS subtitle files found for next episode {ItemId}.", nextEpisode.Id);
            return;
        }

        _logger.LogInformation("Found {Count} ASS subtitle file(s) for next episode. Starting subset generation...", assPaths.Length);

        foreach (string assPath in assPaths)
        {
            try
            {
                _logger.LogInformation("Prefetch subsetting: {AssPath}", assPath);
                await _cacheService.GetOrGenerateSubtitleAsync(nextEpisode.Id, assPath, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to prefetch subtitle: {AssPath}", assPath);
            }
        }

        _logger.LogInformation("Prefetch subsetting completed for next episode: {NextEpisodeName}.", nextEpisode.Name);
    }

    private Episode? FindNextEpisode(Episode currentEpisode)
    {
        // 获取同一季的所有剧集
        var parentId = currentEpisode.ParentId;
        if (parentId.Equals(default))
        {
            return null;
        }

        int currentIndex = currentEpisode.IndexNumber ?? 0;

        var children = _libraryManager.GetItemList(new InternalItemsQuery
        {
            ParentId = parentId,
            IncludeItemTypes = [BaseItemKind.Episode]
        });

        // 查找 IndexNumber 刚好大于当前集的下一集
        var nextEpisode = children
            .OfType<Episode>()
            .Where(e => e.IndexNumber.HasValue && e.IndexNumber.Value > currentIndex)
            .OrderBy(e => e.IndexNumber!.Value)
            .FirstOrDefault();

        return nextEpisode;
    }

    /// <summary>
    /// Gets all original (non-subsetted) ASS subtitle file paths for a given video.
    /// </summary>
    /// <param name="video">The video item.</param>
    /// <returns>An array of ASS file paths.</returns>
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Path is determined solely from trusted database objects.")]
    private static string[] GetAllOriginalAssPaths(Video video)
    {
        if (string.IsNullOrEmpty(video.Path))
        {
            return [];
        }

        string videoDir = Path.GetDirectoryName(video.Path) ?? string.Empty;
        string videoNameWithoutExt = Path.GetFileNameWithoutExtension(video.Path);

        try
        {
            if (Directory.Exists(videoDir))
            {
                return Directory.GetFiles(videoDir, videoNameWithoutExt + "*.ass")
                    .Where(f => !f.Contains("subsetted", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }
        }
        catch
        {
            /* 忽略目录读取异常 */
        }

        return [];
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
                _sessionManager.PlaybackProgress -= OnPlaybackProgress;
                _sessionManager.PlaybackStopped -= OnPlaybackStopped;
            }
            catch (Exception)
            {
                // 忽略销毁时的任何依赖异常
            }
        }

        _disposed = true;
    }
}
