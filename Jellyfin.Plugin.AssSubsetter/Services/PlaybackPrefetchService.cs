using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.Helpers;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.Services;

/// <summary>
///     Background service that monitors playback progress and prefetches (subsets)
///     the next episode's ASS subtitles when the user is nearing the end of the current episode.
/// </summary>
public class PlaybackPrefetchService : IHostedService, IDisposable
{
    private readonly SubtitleCacheService _cacheService;
    private readonly Func<PluginConfiguration> _configFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<PlaybackPrefetchService> _logger;
    private readonly ISessionManager _sessionManager;

    /// <summary>
    ///     Tracks which session+item combinations have already triggered a prefetch
    ///     to avoid redundant work. Key format: "{SessionId}_{ItemId}".
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _triggeredPrefetches = new();
    private readonly ConcurrentDictionary<string, Lazy<Task>> _prefetchTasks = new(StringComparer.Ordinal);
    private readonly object _taskGate = new();
    private readonly CancellationTokenSource _stoppingCts = new();

    private bool _disposed;
    private bool _started;
    private bool _stopping;

    /// <summary>
    ///     Initializes a new instance of the <see cref="PlaybackPrefetchService" /> class.
    /// </summary>
    /// <param name="sessionManager">The session manager instance.</param>
    /// <param name="libraryManager">The library manager instance.</param>
    /// <param name="cacheService">The subtitle cache service.</param>
    /// <param name="configFactory">The plugin configuration factory.</param>
    /// <param name="logger">The logger instance.</param>
    public PlaybackPrefetchService(
        ISessionManager sessionManager,
        ILibraryManager libraryManager,
        SubtitleCacheService cacheService,
        Func<PluginConfiguration> configFactory,
        ILogger<PlaybackPrefetchService> logger)
    {
        _sessionManager = sessionManager;
        _libraryManager = libraryManager;
        _cacheService = cacheService;
        _configFactory = configFactory;
        _logger = logger;
    }

    private PluginConfiguration Config => _configFactory();

    /// <summary>
    ///     Gets the number of currently tracked prefetch tasks.
    /// </summary>
    internal int ActivePrefetchTaskCount => _prefetchTasks.Count;

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return Task.CompletedTask;
        }

        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        _started = true;
        _logger.LogInformation("[AssSubsetter] PlaybackPrefetchService started.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _sessionManager.PlaybackProgress -= OnPlaybackProgress;
            _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        }
        catch (ObjectDisposedException)
        {
            // Ignore exceptions if the manager is already disposed during shutdown
        }

        Task[] tasks;
        lock (_taskGate)
        {
            _stopping = true;
            tasks = _prefetchTasks.Values
                .Where(static lazy => lazy.IsValueCreated)
                .Select(static lazy => lazy.Value)
                .ToArray();
        }

        await _stoppingCts.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        if (!Config.EnablePrefetchSubsetting)
        {
            return;
        }

        if (e.Item is not Episode episode)
        {
            return;
        }

        long? positionTicks = e.PlaybackPositionTicks;
        long? runtimeTicks = episode.RunTimeTicks;

        if (positionTicks is null or <= 0 || runtimeTicks is null or <= 0)
        {
            return;
        }

        double progressPercent = (double)positionTicks.Value / runtimeTicks.Value * 100.0;

        if (progressPercent < Config.PrefetchTriggerPercent)
        {
            return;
        }

        string sessionId = e.Session?.Id ?? string.Empty;
        string prefetchKey = $"{sessionId}_{episode.Id}";

        Lazy<Task>? candidate = null;
        candidate = new Lazy<Task>(
            () => Task.Run(
                () => RunTrackedPrefetchAsync(prefetchKey, candidate!, episode),
                CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication);
        lock (_taskGate)
        {
            if (_stopping || !_triggeredPrefetches.TryAdd(prefetchKey, 0))
            {
                return;
            }

            Lazy<Task> tracked = _prefetchTasks.GetOrAdd(prefetchKey, candidate);
            if (!ReferenceEquals(tracked, candidate))
            {
                return;
            }

            _ = candidate.Value;
        }

        _logger.LogInformation(
            "[AssSubsetter] Playback progress for episode {EpisodeName} (S{Season}E{Episode}) reached {Percent:F1}%, triggering prefetch for next episode.",
            episode.Name,
            episode.ParentIndexNumber,
            episode.IndexNumber,
            progressPercent);
    }

    private async Task RunTrackedPrefetchAsync(string prefetchKey, Lazy<Task> trackedTask, Episode episode)
    {
        try
        {
            await PrefetchNextEpisodeAsync(episode, _stoppingCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stoppingCts.IsCancellationRequested)
        {
            _logger.LogInformation("[AssSubsetter] Prefetch cancelled because the application is stopping.");
        }

        // codeql[cs/catch-of-all-exceptions] Justification: Prevent an unobserved background task exception.
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AssSubsetter] Error occurred while prefetching next episode subtitles.");
        }
        finally
        {
            ((ICollection<KeyValuePair<string, Lazy<Task>>>)_prefetchTasks)
                .Remove(new KeyValuePair<string, Lazy<Task>>(prefetchKey, trackedTask));
        }
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
    private async Task PrefetchNextEpisodeAsync(Episode currentEpisode, CancellationToken cancellationToken)
    {
        if (currentEpisode.IndexNumber is null)
        {
            _logger.LogDebug("[AssSubsetter] Current episode has no index number, skipping prefetch.");
            return;
        }

        var nextEpisode = FindNextEpisode(currentEpisode);

        if (nextEpisode is null)
        {
            _logger.LogInformation("[AssSubsetter] No next episode found for {EpisodeName}, skipping prefetch.", currentEpisode.Name);
            return;
        }

        _logger.LogInformation("[AssSubsetter] Found next episode: {NextEpisodeName} (S{Season}E{Episode}). Prefetching all ASS subtitles...", nextEpisode.Name, nextEpisode.ParentIndexNumber, nextEpisode.IndexNumber);

        string[] assPaths = AssPathHelper.GetAllOriginalAssPaths(nextEpisode.Path);

        if (assPaths.Length == 0)
        {
            _logger.LogDebug("[AssSubsetter] No external ASS subtitle files found for next episode {ItemId}.", nextEpisode.Id);
            return;
        }

        _logger.LogInformation("[AssSubsetter] Found {Count} ASS subtitle file(s) for next episode. Starting prefetch...", assPaths.Length);

        foreach (string assPath in assPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _logger.LogInformation("[AssSubsetter] Prefetch processing: {AssPath}", assPath);

                await _cacheService.GetOrGenerateSubtitleAsync(nextEpisode.Id, assPath, nextEpisode.Width, nextEpisode.Height, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "[AssSubsetter] Failed to prefetch subtitle (IO Error): {AssPath}", assPath);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError(ex, "[AssSubsetter] Failed to prefetch subtitle (Permission Error): {AssPath}", assPath);
            }
        }

        _logger.LogInformation("[AssSubsetter] Prefetch completed for next episode: {NextEpisodeName}.", nextEpisode.Name);
    }

    private Episode? FindNextEpisode(Episode currentEpisode)
    {
        var parentId = currentEpisode.ParentId;
        if (parentId.Equals(default))
        {
            return null;
        }

        int currentIndex = currentEpisode.IndexNumber ?? 0;

        var children = _libraryManager.GetItemList(new InternalItemsQuery { ParentId = parentId, IncludeItemTypes = [BaseItemKind.Episode] });

        var nextEpisode = children
            .OfType<Episode>()
            .Where(e => e.IndexNumber.HasValue && e.IndexNumber.Value > currentIndex)
            .OrderBy(e => e.IndexNumber!.Value)
            .FirstOrDefault();

        return nextEpisode;
    }

    /// <summary>
    ///     Releases unmanaged and - optionally - managed resources.
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
            catch (ObjectDisposedException)
            {
                // Ignore exceptions if the manager is already disposed during shutdown
            }

            lock (_taskGate)
            {
                _stopping = true;
            }

            _stoppingCts.Cancel();
            _stoppingCts.Dispose();
        }

        _disposed = true;
    }
}
