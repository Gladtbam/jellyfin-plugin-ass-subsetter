using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.Core;
using Jellyfin.Plugin.AssSubsetter.Helpers;
using Jellyfin.Plugin.AssSubsetter.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.Services;

/// <summary>
///     Service responsible for managing subtitle cache and enforcing LRU capacity limits.
/// </summary>
public class SubtitleCacheService : IHostedService, IDisposable
{
    private readonly AssProcessor _assProcessor;
    private readonly FontCacheManager? _fontCacheManager;
    private readonly AssToSupConverter? _assToSupConverter;
    private readonly Func<PluginConfiguration> _configFactory;

    private readonly AsyncKeyedLocker<string> _fileLocks = new(StringComparer.Ordinal);
    private readonly ILogger<SubtitleCacheService> _logger;
    private readonly SubtitleCacheQuota _quota;
    private readonly MksMuxer? _mksMuxer;
    private readonly BoundedBackgroundScheduler _scheduler;
    private bool _disposed;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SubtitleCacheService" /> class.
    /// </summary>
    /// <param name="configFactory">The plugin configuration factory.</param>
    /// <param name="assProcessor">The ASS processor instance.</param>
    /// <param name="assToSupConverter">The ASS to SUP converter instance.</param>
    /// <param name="cacheFolderPath">Optional custom cache path.</param>
    /// <param name="logger">The logger instance.</param>
    public SubtitleCacheService(Func<PluginConfiguration> configFactory, AssProcessor assProcessor, AssToSupConverter? assToSupConverter, string cacheFolderPath, ILogger<SubtitleCacheService> logger)
        : this(configFactory, assProcessor, assToSupConverter, null, cacheFolderPath, logger)
    {
    }

    /// <summary>
    ///     Initializes a new instance of the <see cref="SubtitleCacheService" /> class.
    /// </summary>
    /// <param name="configFactory">The configuration factory.</param>
    /// <param name="assProcessor">The ASS processor instance.</param>
    /// <param name="assToSupConverter">The ASS to SUP converter instance.</param>
    /// <param name="mksMuxer">The MKS muxer instance.</param>
    /// <param name="cacheFolderPath">Optional custom cache path.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="fontCacheManager">The shared font selection and dependency validator.</param>
    public SubtitleCacheService(
        Func<PluginConfiguration> configFactory,
        AssProcessor assProcessor,
        AssToSupConverter? assToSupConverter,
        MksMuxer? mksMuxer,
        string cacheFolderPath,
        ILogger<SubtitleCacheService> logger,
        FontCacheManager? fontCacheManager = null)
    {
        _configFactory = configFactory;
        _fontCacheManager = fontCacheManager;
        _assProcessor = assProcessor;
        _assToSupConverter = assToSupConverter;
        _mksMuxer = mksMuxer;
        _logger = logger;
        _scheduler = new BoundedBackgroundScheduler(logger);
        CacheFolderPath = !string.IsNullOrWhiteSpace(cacheFolderPath)
            ? cacheFolderPath
            : Plugin.Instance?.PluginCachePath ?? Path.Join(AppContext.BaseDirectory, "Cache");

        if (!Directory.Exists(CacheFolderPath))
        {
            Directory.CreateDirectory(CacheFolderPath);
        }

        _quota = new SubtitleCacheQuota(CacheFolderPath, logger, TimeProvider.System);
    }

    private PluginConfiguration Config => _configFactory();

    private long CacheCapacityBytes => (long)Config.MaxCacheSizeMB * 1024 * 1024;

    /// <summary>
    ///     Gets the cache folder path.
    /// </summary>
    public string CacheFolderPath { get; }

    /// <summary>
    ///     Gets the number of SUP conversions currently tracked by the service.
    /// </summary>
    internal int ActiveBackgroundConversionCount => _scheduler.Count;

    /// <summary>Completes startup of the cache service after its scheduler has been initialized.</summary>
    /// <param name="cancellationToken">The host startup token supplied by the hosted-service contract.</param>
    /// <returns>A completed startup task.</returns>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <summary>Stops the background subtitle conversion scheduler.</summary>
    /// <param name="cancellationToken">A token that limits the wait for the scheduler to stop.</param>
    /// <returns>A task representing shutdown of the background workers.</returns>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _scheduler.StopAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops and disposes the background conversion scheduler.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Releases managed lifecycle resources.
    /// </summary>
    /// <param name="disposing"><c>true</c> to release managed resources.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            _scheduler.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        _disposed = true;
    }

    /// <summary>
    ///     Gets an existing subset subtitle file or generates one on-demand (JIT).
    /// </summary>
    /// <param name="itemId">The media item identifier.</param>
    /// <param name="originalAssPath">The physical path of the original ASS file.</param>
    /// <param name="videoWidth">The video frame width.</param>
    /// <param name="videoHeight">The video frame height.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result containing path and content type.</returns>
    public virtual async Task<SubtitleResult> GetOrGenerateSubtitleAsync(Guid itemId, string originalAssPath, int videoWidth = 0, int videoHeight = 0, CancellationToken cancellationToken = default)
    {
        try
        {
            return await GetOrGenerateSubtitleCoreAsync(itemId, originalAssPath, videoWidth, videoHeight, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Unable to read subtitle cache dependencies for item {ItemId}.", itemId);
            return CreateFallback(originalAssPath);
        }
    }

    private async Task<SubtitleResult> GetOrGenerateSubtitleCoreAsync(Guid itemId, string originalAssPath, int videoWidth, int videoHeight, CancellationToken cancellationToken)
    {
        SubtitleProcessingMode mode = Config.SubtitleMode switch
        {
            SubtitleProcessingMode.ConvertToSup => SubtitleProcessingMode.ConvertToSup,
            SubtitleProcessingMode.GenerateMks => SubtitleProcessingMode.GenerateMks,
            _ => SubtitleProcessingMode.Subsetting
        };
        var request = new CacheRequest(itemId, originalAssPath, mode, videoWidth > 0 ? videoWidth : 1920, videoHeight > 0 ? videoHeight : 1080);
        string cachePath = CaptureCacheIdentity(request).Path;
        if (TryUseCachedFile(request, cachePath))
        {
            return new SubtitleResult(cachePath, request.Format.ContentType, true);
        }

        if (mode == SubtitleProcessingMode.ConvertToSup)
        {
            _scheduler.TryEnqueue(
                cachePath,
                async token => { await GetOrGenerateSupAsync(itemId, originalAssPath, request.Width, request.Height, token).ConfigureAwait(false); });
            return new SubtitleResult(originalAssPath, "text/x-ssa", false);
        }

        string? generatedPath = await GenerateMissingCacheFileAsync(request, cachePath, cancellationToken).ConfigureAwait(false);
        return generatedPath is null ? CreateFallback(originalAssPath) : new SubtitleResult(generatedPath, request.Format.ContentType, true);
    }

    /// <summary>Gets a cached SUP file or generates it from the source subtitle.</summary>
    /// <param name="itemId">The media item identifier.</param>
    /// <param name="originalAssPath">The source subtitle path.</param>
    /// <param name="videoWidth">The SUP rendering width.</param>
    /// <param name="videoHeight">The SUP rendering height.</param>
    /// <param name="cancellationToken">Cancels the conversion or its wait for the cache file.</param>
    /// <returns>The cached SUP path, the configured fallback on conversion failure, or an empty path when the converter is unavailable.</returns>
    protected virtual async Task<string> GetOrGenerateSupAsync(Guid itemId, string originalAssPath, int videoWidth, int videoHeight, CancellationToken cancellationToken = default)
    {
        var request = new CacheRequest(itemId, originalAssPath, SubtitleProcessingMode.ConvertToSup, videoWidth, videoHeight);
        string cachePath = CaptureCacheIdentity(request).Path;
        if (TryUseCachedFile(request, cachePath))
        {
            return cachePath;
        }

        if (_assToSupConverter is null)
        {
            _logger.LogWarning("[AssSubsetter] AssToSupConverter is not available. Cannot convert ASS to SUP.");
            return string.Empty;
        }

        string? generatedPath = await GenerateMissingCacheFileAsync(request, cachePath, cancellationToken).ConfigureAwait(false);
        return generatedPath ?? GetFallbackPath(originalAssPath);
    }

    private (string Path, SubtitleFontDependencies? Dependencies) CaptureCacheIdentity(CacheRequest request)
    {
        int frameRate = request.Mode == SubtitleProcessingMode.ConvertToSup ? Math.Clamp(Config.AssToSupFrameRate, 10, 60) : 0;
        var dependencies = _fontCacheManager?.GetSubtitleDependencies(request.SourcePath);
        string fingerprint = SubtitleCacheFingerprint.Create(
            request.SourcePath,
            request.Mode,
            request.Width,
            request.Height,
            frameRate,
            dependencies?.Fingerprint ?? string.Empty);
        return (Path.Join(CacheFolderPath, $"{request.ItemId:N}_{fingerprint}{request.Format.Extension}"), dependencies);
    }

    private async Task<string?> GenerateMissingCacheFileAsync(CacheRequest request, string cachePath, CancellationToken cancellationToken)
    {
        IDisposable fileLock;
        try
        {
            fileLock = await _fileLocks.LockAsync(cachePath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (request.Mode == SubtitleProcessingMode.GenerateMks)
        {
            return null;
        }

        using (fileLock)
        {
            if (TryUseCachedFile(request, cachePath))
            {
                return cachePath;
            }

            if (request.Mode == SubtitleProcessingMode.GenerateMks && _mksMuxer is null)
            {
                _logger.LogWarning("[AssSubsetter] MKS muxer is not available for item {ItemId}.", request.ItemId);
                return null;
            }

            var (currentPath, dependencies) = CaptureCacheIdentity(request);
            if (!StringComparer.Ordinal.Equals(cachePath, currentPath))
            {
                return null;
            }

            using var validation = dependencies is null ? null : _fontCacheManager?.BeginDependencyValidation(dependencies);
            bool success = await AtomicCacheFile.WriteAsync(
                cachePath,
                async (temporaryPath, token) =>
                {
                    bool generated = await GenerateCacheArtifactAsync(request, temporaryPath, token).ConfigureAwait(false);
                    return generated && (validation?.IsComplete ?? true)
                        && (request.Mode != SubtitleProcessingMode.GenerateMks || IsReadyFile(request, temporaryPath))
                        && StringComparer.Ordinal.Equals(cachePath, CaptureCacheIdentity(request).Path);
                },
                _logger,
                cancellationToken,
                (temporaryPath, finalPath, token) => _quota.Publish(temporaryPath, finalPath, CacheCapacityBytes, token)).ConfigureAwait(false);

            if (success && IsReadyFile(request, cachePath))
            {
                TouchFile(cachePath);
                return cachePath;
            }

            _logger.LogWarning("[AssSubsetter] Subtitle cache generation failed in {Mode} for item {ItemId}.", request.Mode, request.ItemId);
            return null;
        }
    }

    private async Task<bool> GenerateCacheArtifactAsync(CacheRequest request, string outputPath, CancellationToken cancellationToken)
    {
        if (request.Mode == SubtitleProcessingMode.GenerateMks)
        {
            var artifact = await _assProcessor.GenerateSubsetArtifactAsync(request.SourcePath, cancellationToken).ConfigureAwait(false);
            if (artifact is null)
            {
                return false;
            }

            EnsureCapacity(() => Math.Max(artifact.AssContent.Length + artifact.Fonts.Sum(font => (long)font.Data.Length), 1));
            return await _mksMuxer!.MuxAsync(artifact, outputPath, cancellationToken).ConfigureAwait(false);
        }

        EnsureCapacity(() =>
        {
            var source = new FileInfo(request.SourcePath);
            long sourceBytes = source.Exists ? source.Length : 2 * 1024 * 1024;
            return request.Mode == SubtitleProcessingMode.ConvertToSup ? sourceBytes * 5 : sourceBytes;
        });
        return request.Mode == SubtitleProcessingMode.ConvertToSup
            ? await _assToSupConverter!.ConvertAsync(request.SourcePath, outputPath, request.Width, request.Height, cancellationToken).ConfigureAwait(false)
            : await _assProcessor.GenerateSubsetFontAsync(request.SourcePath, outputPath, cancellationToken).ConfigureAwait(false);
    }

    private void EnsureCapacity(Func<long> estimateBytes)
    {
        try
        {
            _quota.EnsureCapacity(estimateBytes(), CacheCapacityBytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "[AssSubsetter] Could not check subtitle cache capacity.");
        }
    }

    private bool TryUseCachedFile(CacheRequest request, string path)
    {
        if (!IsReadyFile(request, path))
        {
            return false;
        }

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation("[AssSubsetter] Subtitle cache hit in {Mode} for item {ItemId}.", request.Mode, request.ItemId);
        }

        TouchFile(path);
        return true;
    }

    private static bool IsReadyFile(CacheRequest request, string path)
    {
        if (request.Mode != SubtitleProcessingMode.GenerateMks)
        {
            return File.Exists(path);
        }

        var file = new FileInfo(path);
        return file.Exists && file.Length > 0;
    }

    private static void TouchFile(string path)
    {
        try
        {
            File.SetLastAccessTime(path, DateTime.Now);
        }
        catch (IOException)
        {
            /* Ignore */
        }
        catch (UnauthorizedAccessException)
        {
            /* Ignore */
        }
    }

    private SubtitleResult CreateFallback(string originalAssPath) => new(GetFallbackPath(originalAssPath), "text/x-ssa", false);

    private string GetFallbackPath(string originalAssPath) => Config.FallbackToOriginalOnError ? originalAssPath : string.Empty;

    private readonly record struct CacheRequest(Guid ItemId, string SourcePath, SubtitleProcessingMode Mode, int Width, int Height)
    {
        internal (string Extension, string ContentType) Format => Mode switch
        {
            SubtitleProcessingMode.ConvertToSup => (".sup", "application/octet-stream"),
            SubtitleProcessingMode.GenerateMks => (".mks", "video/x-matroska"),
            _ => (".ass", "text/x-ssa")
        };
    }
}
