using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.Helpers;
using Jellyfin.Plugin.AssSubsetter.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.ScheduledTasks;

/// <summary>
///     Background service that tracks library scans and preemptively generates subset fonts.
///     Uses a bounded channel to prevent task explosion during bulk library scans.
/// </summary>
public class LibraryScanTracker : IHostedService, IDisposable
{
    private readonly SubtitleCacheService _cacheService;
    private readonly Func<PluginConfiguration> _configFactory;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<LibraryScanTracker> _logger;

    private readonly Channel<Video> _processQueue = Channel.CreateBounded<Video>(
        new BoundedChannelOptions(256) { FullMode = BoundedChannelFullMode.DropOldest });

    private Task? _consumerTask;

    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <summary>
    ///     Initializes a new instance of the <see cref="LibraryScanTracker" /> class.
    /// </summary>
    /// <param name="libraryManager">The library manager instance.</param>
    /// <param name="cacheService">The subtitle cache service.</param>
    /// <param name="configFactory">The plugin configuration factory.</param>
    /// <param name="logger">The logger instance.</param>
    public LibraryScanTracker(
        ILibraryManager libraryManager,
        SubtitleCacheService cacheService,
        Func<PluginConfiguration> configFactory,
        ILogger<LibraryScanTracker> logger)
    {
        _libraryManager = libraryManager;
        _cacheService = cacheService;
        _configFactory = configFactory;
        _logger = logger;
    }

    private PluginConfiguration Config => _configFactory();

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
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
        if (!Config.EnableAutoScanProcessing || item is not Video video)
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
                foreach (string originalAssPath in AssPathHelper.GetAllOriginalAssPaths(video.Path))
                {
                    _logger.LogDebug("[AssSubsetter] Auto-scan processing item: {ItemId}", video.Id);

                    try
                    {
                        await _cacheService.GetOrGenerateSubtitleAsync(video.Id, originalAssPath, video.Width, video.Height, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }

                    // codeql[cs/catch-of-all-exceptions] Justification: A failed subtitle must not prevent other subtitles for the same item from being processed.
                    catch (Exception ex)
                    {
                        _logger.LogError(
                            ex,
                            "[AssSubsetter] Unexpected error auto-generating subtitle {SubtitlePath} for item {ItemId}",
                            originalAssPath,
                            video.Id);
                    }
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
