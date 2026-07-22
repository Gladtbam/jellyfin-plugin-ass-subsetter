using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    private readonly AssToSupConverter? _assToSupConverter;
    private readonly Func<PluginConfiguration> _configFactory;

    private readonly AsyncKeyedLocker<string> _fileLocks = new(StringComparer.Ordinal);
    private readonly ILogger<SubtitleCacheService> _logger;
    private readonly MksMuxer? _mksMuxer;
    private readonly ConcurrentDictionary<string, Lazy<Task>> _backgroundConversions = new(StringComparer.Ordinal);
    private readonly object _backgroundGate = new();
    private readonly CancellationTokenSource _stoppingCts = new();
    private bool _disposed;
    private bool _stopping;

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
    public SubtitleCacheService(
        Func<PluginConfiguration> configFactory,
        AssProcessor assProcessor,
        AssToSupConverter? assToSupConverter,
        MksMuxer? mksMuxer,
        string cacheFolderPath,
        ILogger<SubtitleCacheService> logger)
    {
        _configFactory = configFactory;
        _assProcessor = assProcessor;
        _assToSupConverter = assToSupConverter;
        _mksMuxer = mksMuxer;
        _logger = logger;
        CacheFolderPath = !string.IsNullOrWhiteSpace(cacheFolderPath)
            ? cacheFolderPath
            : Plugin.Instance?.PluginCachePath ?? Path.Join(AppContext.BaseDirectory, "Cache");

        if (!Directory.Exists(CacheFolderPath))
        {
            Directory.CreateDirectory(CacheFolderPath);
        }
    }

    private PluginConfiguration Config => _configFactory();

    /// <summary>
    ///     Gets the cache folder path.
    /// </summary>
    public string CacheFolderPath { get; }

    /// <summary>
    ///     Gets the number of SUP conversions currently tracked by the service.
    /// </summary>
    internal int ActiveBackgroundConversionCount => _backgroundConversions.Count;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task[] tasks;
        lock (_backgroundGate)
        {
            _stopping = true;
            tasks = _backgroundConversions.Values
                .Where(static lazy => lazy.IsValueCreated)
                .Select(static lazy => lazy.Value)
                .ToArray();
        }

        await _stoppingCts.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
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
            lock (_backgroundGate)
            {
                _stopping = true;
            }

            _stoppingCts.Cancel();
            _stoppingCts.Dispose();
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
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Paths are guided by sanitized database-derived item IDs and hardcoded directories.")]
    public virtual async Task<SubtitleResult> GetOrGenerateSubtitleAsync(Guid itemId, string originalAssPath, int videoWidth = 0, int videoHeight = 0, CancellationToken cancellationToken = default)
    {
        videoWidth = videoWidth > 0 ? videoWidth : 1920;
        videoHeight = videoHeight > 0 ? videoHeight : 1080;

        if (Config.SubtitleMode == SubtitleProcessingMode.GenerateMks)
        {
            return await GetOrGenerateMksAsync(itemId, originalAssPath, cancellationToken).ConfigureAwait(false);
        }

        if (Config.SubtitleMode == SubtitleProcessingMode.ConvertToSup)
        {
            string supCachePath = GetSupCachePath(itemId, originalAssPath, videoWidth, videoHeight);

            if (File.Exists(supCachePath))
            {
                _logger.LogInformation("[AssSubsetter] SUP cache hit for item {ItemId}. Serving cached SUP.", itemId);
                TouchFile(supCachePath);
                return new SubtitleResult(supCachePath, "application/octet-stream", true);
            }

            // Cache miss — trigger a conversion owned by this hosted service rather than the HTTP request.
            TriggerBackgroundSupConversion(supCachePath, itemId, originalAssPath, videoWidth, videoHeight);

            // Return original ASS as fallback
            return new SubtitleResult(originalAssPath, "text/x-ssa", false);
        }

        string cacheFilePath = GetSubsetCachePath(itemId, originalAssPath);

        if (File.Exists(cacheFilePath))
        {
            _logger.LogInformation("[AssSubsetter] Cache hit: Returning existing subsetted subtitle for item {ItemId}", itemId);
            TouchFile(cacheFilePath);

            return new SubtitleResult(cacheFilePath, "text/x-ssa", true);
        }

        using (await _fileLocks.LockAsync(cacheFilePath, cancellationToken).ConfigureAwait(false))
        {
            if (File.Exists(cacheFilePath))
            {
                _logger.LogInformation("[AssSubsetter] Cache hit (after lock): Returning existing subsetted subtitle for item {ItemId}", itemId);
                TouchFile(cacheFilePath);

                return new SubtitleResult(cacheFilePath, "text/x-ssa", true);
            }

            try
            {
                long requiredSpace = File.Exists(originalAssPath) ? new FileInfo(originalAssPath).Length : 2 * 1024 * 1024;
                EnforceCapacityLimit(requiredSpace);
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "[AssSubsetter] IO Error occurred during LRU cache eviction.");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError(ex, "[AssSubsetter] Permission Error occurred during LRU cache eviction.");
            }

            _logger.LogInformation("[AssSubsetter] Cache miss for item {ItemId}. Triggering on-demand JIT subsetting...", itemId);
            bool success = await _assProcessor.GenerateSubsetFontAsync(originalAssPath, cacheFilePath, cancellationToken).ConfigureAwait(false);

            if (success && File.Exists(cacheFilePath))
            {
                TouchFile(cacheFilePath);

                return new SubtitleResult(cacheFilePath, "text/x-ssa", true);
            }

            _logger.LogWarning("[AssSubsetter] JIT subsetting failed for item {ItemId}. Falling back.", itemId);
            return new SubtitleResult(Config.FallbackToOriginalOnError ? originalAssPath : string.Empty, "text/x-ssa", false);
        }
    }

    /// <summary>
    ///     Gets the expected SUP cache file path for a given item, without triggering conversion.
    /// </summary>
    /// <param name="itemId">The media item identifier.</param>
    /// <param name="originalAssPath">The physical path of the original ASS file.</param>
    /// <param name="videoWidth">The SUP rendering width.</param>
    /// <param name="videoHeight">The SUP rendering height.</param>
    /// <returns>The expected cache file path for the SUP file.</returns>
    private string GetSupCachePath(Guid itemId, string originalAssPath, int videoWidth, int videoHeight)
    {
        string fingerprint = SubtitleCacheFingerprint.Create(
            originalAssPath,
            SubtitleProcessingMode.ConvertToSup,
            videoWidth,
            videoHeight,
            Math.Clamp(Config.AssToSupFrameRate, 10, 60));
        return Path.Join(CacheFolderPath, $"{itemId:N}_{fingerprint}.sup");
    }

    private string GetMksCachePath(Guid itemId, string originalAssPath)
    {
        string fingerprint = SubtitleCacheFingerprint.Create(originalAssPath, SubtitleProcessingMode.GenerateMks);
        return Path.Join(CacheFolderPath, $"{itemId:N}_{fingerprint}.mks");
    }

    /// <summary>
    ///     Gets the expected ASS subset cache file path for a given item.
    /// </summary>
    /// <param name="itemId">The media item identifier.</param>
    /// <param name="originalAssPath">The physical path of the original ASS file.</param>
    /// <returns>The expected cache file path for the subsetted ASS file.</returns>
    private string GetSubsetCachePath(Guid itemId, string originalAssPath)
    {
        string fingerprint = SubtitleCacheFingerprint.Create(originalAssPath, SubtitleProcessingMode.Subsetting);
        return Path.Join(CacheFolderPath, $"{itemId:N}_{fingerprint}.ass");
    }

    /// <summary>
    ///     Triggers a background ASS to SUP conversion if one is not already in progress for the given item.
    ///     This is a fire-and-forget operation; the conversion runs on a background thread.
    /// </summary>
    /// <param name="conversionKey">The versioned cache output path used for deduplication.</param>
    /// <param name="itemId">The media item identifier.</param>
    /// <param name="originalAssPath">The physical path of the original ASS file.</param>
    /// <param name="videoWidth">Video frame width.</param>
    /// <param name="videoHeight">Video frame height.</param>
    private void TriggerBackgroundSupConversion(string conversionKey, Guid itemId, string originalAssPath, int videoWidth, int videoHeight)
    {
        Lazy<Task>? candidate = null;
        candidate = new Lazy<Task>(
            () => Task.Run(
                () => RunTrackedSupConversionAsync(
                    conversionKey,
                    candidate!,
                    itemId,
                    originalAssPath,
                    videoWidth,
                    videoHeight),
                CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication);
        Lazy<Task> tracked;
        lock (_backgroundGate)
        {
            if (_stopping)
            {
                return;
            }

            tracked = _backgroundConversions.GetOrAdd(conversionKey, candidate);
            if (ReferenceEquals(tracked, candidate))
            {
                _ = candidate.Value;
            }
        }

        if (!ReferenceEquals(tracked, candidate))
        {
            _logger.LogInformation("[AssSubsetter] Background SUP conversion already in progress for item {ItemId}. Skipping.", itemId);
            return;
        }

        _logger.LogInformation("[AssSubsetter] Triggering background ASS to SUP conversion for item {ItemId}...", itemId);
    }

    private async Task RunTrackedSupConversionAsync(
        string conversionKey,
        Lazy<Task> trackedTask,
        Guid itemId,
        string originalAssPath,
        int videoWidth,
        int videoHeight)
    {
        try
        {
            await GetOrGenerateSupAsync(itemId, originalAssPath, videoWidth, videoHeight, _stoppingCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stoppingCts.IsCancellationRequested)
        {
            _logger.LogInformation("[AssSubsetter] Background SUP conversion cancelled for item {ItemId} (application stopping).", itemId);
        }

        // codeql[cs/catch-of-all-exceptions] Justification: Prevent an unobserved background task exception.
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AssSubsetter] Background SUP conversion failed for item {ItemId}.", itemId);
        }
        finally
        {
            ((ICollection<KeyValuePair<string, Lazy<Task>>>)_backgroundConversions)
                .Remove(new KeyValuePair<string, Lazy<Task>>(conversionKey, trackedTask));
        }
    }

    /// <summary>
    ///     Gets an existing SUP subtitle file or generates one on-demand via ASS to SUP conversion.
    /// </summary>
    /// <param name="itemId">The media item identifier.</param>
    /// <param name="originalAssPath">The physical path of the original ASS file.</param>
    /// <param name="videoWidth">Video frame width.</param>
    /// <param name="videoHeight">Video frame height.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The path to the cached SUP file, or empty string on failure.</returns>
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Paths are guided by sanitized database-derived item IDs and hardcoded directories.")]
    protected virtual async Task<string> GetOrGenerateSupAsync(Guid itemId, string originalAssPath, int videoWidth, int videoHeight, CancellationToken cancellationToken = default)
    {
        string cacheFilePath = GetSupCachePath(itemId, originalAssPath, videoWidth, videoHeight);

        if (File.Exists(cacheFilePath))
        {
            _logger.LogInformation("[AssSubsetter] SUP cache hit: Returning existing converted subtitle for item {ItemId}", itemId);
            TouchFile(cacheFilePath);
            return cacheFilePath;
        }

        if (_assToSupConverter == null)
        {
            _logger.LogWarning("[AssSubsetter] AssToSupConverter is not available. Cannot convert ASS to SUP.");
            return string.Empty;
        }

        using (await _fileLocks.LockAsync(cacheFilePath, cancellationToken).ConfigureAwait(false))
        {
            if (File.Exists(cacheFilePath))
            {
                _logger.LogInformation("[AssSubsetter] SUP cache hit (after lock): Returning existing converted subtitle for item {ItemId}", itemId);
                TouchFile(cacheFilePath);
                return cacheFilePath;
            }

            try
            {
                long requiredSpace = File.Exists(originalAssPath) ? new FileInfo(originalAssPath).Length * 5 : 10 * 1024 * 1024;
                EnforceCapacityLimit(requiredSpace);
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "[AssSubsetter] IO Error occurred during LRU cache eviction.");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError(ex, "[AssSubsetter] Permission Error occurred during LRU cache eviction.");
            }

            _logger.LogInformation("[AssSubsetter] SUP cache miss for item {ItemId}. Triggering ASS to SUP conversion...", itemId);
            bool success = await _assToSupConverter.ConvertAsync(originalAssPath, cacheFilePath, videoWidth, videoHeight, cancellationToken).ConfigureAwait(false);

            if (success && File.Exists(cacheFilePath))
            {
                TouchFile(cacheFilePath);
                return cacheFilePath;
            }

            _logger.LogWarning("[AssSubsetter] ASS to SUP conversion failed for item {ItemId}. Falling back.", itemId);
            return Config.FallbackToOriginalOnError ? originalAssPath : string.Empty;
        }
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

    private async Task<SubtitleResult> GetOrGenerateMksAsync(Guid itemId, string originalAssPath, CancellationToken cancellationToken)
    {
        string cacheFilePath = GetMksCachePath(itemId, originalAssPath);
        if (File.Exists(cacheFilePath) && new FileInfo(cacheFilePath).Length > 0)
        {
            _logger.LogInformation("[AssSubsetter] MKS cache hit for item {ItemId}.", itemId);
            TouchFile(cacheFilePath);
            return new SubtitleResult(cacheFilePath, "video/x-matroska", true);
        }

        IDisposable fileLock;
        try
        {
            fileLock = await _fileLocks.LockAsync(cacheFilePath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return CreateMksFallback(originalAssPath);
        }

        using (fileLock)
        {
            if (File.Exists(cacheFilePath) && new FileInfo(cacheFilePath).Length > 0)
            {
                TouchFile(cacheFilePath);
                return new SubtitleResult(cacheFilePath, "video/x-matroska", true);
            }

            if (_mksMuxer == null)
            {
                _logger.LogWarning("[AssSubsetter] MKS muxer is not available for item {ItemId}.", itemId);
                return CreateMksFallback(originalAssPath);
            }

            var artifact = await _assProcessor.GenerateSubsetArtifactAsync(originalAssPath, cancellationToken).ConfigureAwait(false);
            if (artifact == null)
            {
                return CreateMksFallback(originalAssPath);
            }

            try
            {
                long requiredSpace = artifact.AssContent.Length + artifact.Fonts.Sum(font => (long)font.Data.Length);
                EnforceCapacityLimit(Math.Max(requiredSpace, 1));
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "[AssSubsetter] IO error during MKS cache eviction.");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError(ex, "[AssSubsetter] Permission error during MKS cache eviction.");
            }

            bool success = await _mksMuxer.MuxAsync(artifact, cacheFilePath, cancellationToken).ConfigureAwait(false);
            if (success && File.Exists(cacheFilePath) && new FileInfo(cacheFilePath).Length > 0)
            {
                TouchFile(cacheFilePath);
                return new SubtitleResult(cacheFilePath, "video/x-matroska", true);
            }

            return CreateMksFallback(originalAssPath);
        }
    }

    private SubtitleResult CreateMksFallback(string originalAssPath)
    {
        return new SubtitleResult(Config.FallbackToOriginalOnError ? originalAssPath : string.Empty, "text/x-ssa", false);
    }

    private void EnforceCapacityLimit(long requiredSpace)
    {
        var dirInfo = new DirectoryInfo(CacheFolderPath);
        long maxCacheSizeInBytes = (long)Config.MaxCacheSizeMB * 1024 * 1024;
        long currentSize = dirInfo.EnumerateFiles()
            .Where(IsManagedCacheFile)
            .Sum(f => f.Length);

        if (currentSize + requiredSpace <= maxCacheSizeInBytes)
        {
            return;
        }

        _logger.LogInformation("[AssSubsetter] Cache folder quota exceeded ({Current}MB / {Max}MB). Running LRU eviction...", currentSize / 1024 / 1024, Config.MaxCacheSizeMB);

        var oldestFiles = dirInfo.GetFiles()
            .Where(IsManagedCacheFile)
            .OrderBy(f => f.LastAccessTime)
            .ToList();

        foreach (var file in oldestFiles)
        {
            if (currentSize + requiredSpace <= maxCacheSizeInBytes)
            {
                break;
            }

            try
            {
                long fileSize = file.Length;
                file.Delete();
                currentSize -= fileSize;
                _logger.LogDebug("[AssSubsetter] LRU evicted oldest cache file: {Name}", file.Name);
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "[AssSubsetter] Failed to delete evicted cache file: {Name} (IO Error)", file.Name);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError(ex, "[AssSubsetter] Failed to delete evicted cache file: {Name} (Permission Error)", file.Name);
            }
        }
    }

    private static bool IsManagedCacheFile(FileInfo file)
    {
        return file.Extension.Equals(".ass", StringComparison.OrdinalIgnoreCase) ||
               file.Extension.Equals(".sup", StringComparison.OrdinalIgnoreCase) ||
               file.Extension.Equals(".mks", StringComparison.OrdinalIgnoreCase);
    }
}
