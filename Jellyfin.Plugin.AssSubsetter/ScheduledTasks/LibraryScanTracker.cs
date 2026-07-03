using System;
using System.Threading;
using System.Threading.Channels;
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
/// Uses a bounded channel to prevent task explosion during bulk library scans.
/// </summary>
public class LibraryScanTracker : IHostedService, IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly SubtitleCacheService _cacheService;
    private readonly PluginConfiguration _config;
    private readonly ILogger<LibraryScanTracker> _logger;

    private readonly Channel<Video> _processQueue = Channel.CreateBounded<Video>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });

    private CancellationTokenSource? _cts;
    private Task? _consumerTask;
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

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _consumerTask = Task.Run(() => ConsumeQueueAsync(_cts.Token), _cts.Token);

        _logger.LogInformation("[AssSubsetter] LibraryScanTracker started.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            _libraryManager.ItemAdded -= OnItemAdded;
            _libraryManager.ItemUpdated -= OnItemUpdated;
        }
        catch (ObjectDisposedException)
        {
            // Ignore exceptions if the manager is already disposed during shutdown
        }

        _processQueue.Writer.TryComplete();

        if (_cts != null)
        {
            await _cts.CancelAsync().ConfigureAwait(false);
        }

        if (_consumerTask != null)
        {
            try
            {
                await _consumerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
            }
        }
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e) => EnqueueItem(e.Item);

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e) => EnqueueItem(e.Item);

    private void EnqueueItem(BaseItem item)
    {
        if (!_config.EnableAutoScanProcessing || item is not Video video)
        {
            return;
        }

        _processQueue.Writer.TryWrite(video);
    }

    private async Task ConsumeQueueAsync(CancellationToken cancellationToken)
    {
        await foreach (var video in _processQueue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                string originalAssPath = AssPathHelper.GetOriginalAssPath(video.Path);
                if (!string.IsNullOrEmpty(originalAssPath))
                {
                    _logger.LogDebug("[AssSubsetter] Auto-scan processing item: {ItemId}", video.Id);

                    await _cacheService.GetOrGenerateSubtitleAsync(video.Id, originalAssPath, video.Width, video.Height, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            // codeql[cs/catch-of-all-exceptions] Justification: Prevent background task loop termination.
            catch (Exception ex)
            {
                _logger.LogError(ex, "[AssSubsetter] Unexpected error auto-generating subtitle for item {ItemId}", video.Id);
            }
        }
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
                _libraryManager.ItemAdded -= OnItemAdded;
                _libraryManager.ItemUpdated -= OnItemUpdated;
            }
            catch (ObjectDisposedException)
            {
                // Ignore exceptions if the manager is already disposed during shutdown
            }

            _cts?.Dispose();
        }

        _disposed = true;
    }
}
