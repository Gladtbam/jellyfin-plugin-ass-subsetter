using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.Core;
using Jellyfin.Plugin.AssSubsetter.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.Services;

/// <summary>
///     Service responsible for managing subtitle cache and enforcing LRU capacity limits.
/// </summary>
public class SubtitleCacheService
{
    private readonly AssProcessor _assProcessor;
    private readonly AssToSupConverter? _assToSupConverter;
    private readonly Func<PluginConfiguration> _configFactory;

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _fileLocks = new();
    private readonly ILogger<SubtitleCacheService> _logger;
    private readonly ConcurrentDictionary<Guid, byte> _pendingConversions = new();

    /// <summary>
    ///     Initializes a new instance of the <see cref="SubtitleCacheService" /> class.
    /// </summary>
    /// <param name="configFactory">The plugin configuration factory.</param>
    /// <param name="assProcessor">The ASS processor instance.</param>
    /// <param name="assToSupConverter">The ASS to SUP converter instance.</param>
    /// <param name="cacheFolderPath">Optional custom cache path.</param>
    /// <param name="logger">The logger instance.</param>
    public SubtitleCacheService(Func<PluginConfiguration> configFactory, AssProcessor assProcessor, AssToSupConverter? assToSupConverter, string cacheFolderPath, ILogger<SubtitleCacheService> logger)
    {
        _configFactory = configFactory;
        _assProcessor = assProcessor;
        _assToSupConverter = assToSupConverter;
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

        if (Config.SubtitleMode == SubtitleProcessingMode.ConvertToSup)
        {
            string supCachePath = GetSupCachePath(itemId, originalAssPath);

            if (File.Exists(supCachePath))
            {
                _logger.LogInformation("[AssSubsetter] SUP cache hit for item {ItemId}. Serving cached SUP.", itemId);
                TouchFile(supCachePath);
                return new SubtitleResult(supCachePath, "application/octet-stream", true);
            }

            // Cache miss — trigger background conversion (fire-and-forget)
            // Use CancellationToken.None because this background task must outlive the HTTP request.
            TriggerBackgroundSupConversion(itemId, originalAssPath, videoWidth, videoHeight, CancellationToken.None);

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

        var fileLock = _fileLocks.GetOrAdd(itemId, _ => new SemaphoreSlim(1, 1));
        await fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
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
        finally
        {
            fileLock.Release();
        }
    }

    /// <summary>
    ///     Gets the expected SUP cache file path for a given item, without triggering conversion.
    /// </summary>
    /// <param name="itemId">The media item identifier.</param>
    /// <param name="originalAssPath">The physical path of the original ASS file.</param>
    /// <returns>The expected cache file path for the SUP file.</returns>
    private string GetSupCachePath(Guid itemId, string originalAssPath)
    {
        string safeFileName = Path.GetFileNameWithoutExtension(originalAssPath);
        return Path.Join(CacheFolderPath, $"{itemId:N}_{safeFileName}.sup");
    }

    /// <summary>
    ///     Gets the expected ASS subset cache file path for a given item.
    /// </summary>
    /// <param name="itemId">The media item identifier.</param>
    /// <param name="originalAssPath">The physical path of the original ASS file.</param>
    /// <returns>The expected cache file path for the subsetted ASS file.</returns>
    private string GetSubsetCachePath(Guid itemId, string originalAssPath)
    {
        string safeFileName = Path.GetFileName(originalAssPath);
        return Path.Join(CacheFolderPath, $"{itemId:N}_{safeFileName}");
    }

    /// <summary>
    ///     Triggers a background ASS to SUP conversion if one is not already in progress for the given item.
    ///     This is a fire-and-forget operation; the conversion runs on a background thread.
    /// </summary>
    /// <param name="itemId">The media item identifier.</param>
    /// <param name="originalAssPath">The physical path of the original ASS file.</param>
    /// <param name="videoWidth">Video frame width.</param>
    /// <param name="videoHeight">Video frame height.</param>
    /// <param name="stoppingToken">A cancellation token tied to application lifetime, not HTTP requests.</param>
    private void TriggerBackgroundSupConversion(Guid itemId, string originalAssPath, int videoWidth, int videoHeight, CancellationToken stoppingToken)
    {
        if (!_pendingConversions.TryAdd(itemId, 0))
        {
            _logger.LogInformation("[AssSubsetter] Background SUP conversion already in progress for item {ItemId}. Skipping.", itemId);
            return;
        }

        _logger.LogInformation("[AssSubsetter] Triggering background ASS to SUP conversion for item {ItemId}...", itemId);

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await GetOrGenerateSupAsync(itemId, originalAssPath, videoWidth, videoHeight, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("[AssSubsetter] Background SUP conversion cancelled for item {ItemId} (application stopping).", itemId);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[AssSubsetter] Background SUP conversion failed for item {ItemId}.", itemId);
                }
                finally
                {
                    _pendingConversions.TryRemove(itemId, out _);
                }
            },
            stoppingToken);
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
        string cacheFilePath = GetSupCachePath(itemId, originalAssPath);

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

        var fileLock = _fileLocks.GetOrAdd(itemId, _ => new SemaphoreSlim(1, 1));
        await fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
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
        finally
        {
            fileLock.Release();
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

    private void EnforceCapacityLimit(long requiredSpace)
    {
        var dirInfo = new DirectoryInfo(CacheFolderPath);
        long maxCacheSizeInBytes = (long)Config.MaxCacheSizeMB * 1024 * 1024;
        long currentSize = dirInfo.EnumerateFiles()
            .Where(f => f.Extension.Equals(".ass", StringComparison.OrdinalIgnoreCase) ||
                        f.Extension.Equals(".sup", StringComparison.OrdinalIgnoreCase))
            .Sum(f => f.Length);

        if (currentSize + requiredSpace <= maxCacheSizeInBytes)
        {
            return;
        }

        _logger.LogInformation("[AssSubsetter] Cache folder quota exceeded ({Current}MB / {Max}MB). Running LRU eviction...", currentSize / 1024 / 1024, Config.MaxCacheSizeMB);

        var oldestFiles = dirInfo.GetFiles()
            .Where(f => f.Extension.Equals(".ass", StringComparison.OrdinalIgnoreCase) ||
                        f.Extension.Equals(".sup", StringComparison.OrdinalIgnoreCase))
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
}
