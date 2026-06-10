using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.Services;

/// <summary>
/// Service responsible for managing subtitle cache and enforcing LRU capacity limits.
/// </summary>
public class SubtitleCacheService
{
    private readonly string _cacheFolderPath;
    private readonly PluginConfiguration _config;
    private readonly AssProcessor _assProcessor;
    private readonly ILogger<SubtitleCacheService> _logger;

    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _fileLocks = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleCacheService"/> class.
    /// </summary>
    /// <param name="config">The plugin configuration instance.</param>
    /// <param name="assProcessor">The ASS processor instance.</param>
    /// <param name="cacheFolderPath">The custom cache folder path. Pass null or empty to use plugin default path.</param>
    /// <param name="logger">The logger instance.</param>
    public SubtitleCacheService(PluginConfiguration config, AssProcessor assProcessor, string cacheFolderPath, ILogger<SubtitleCacheService> logger)
    {
        _config = config;
        _assProcessor = assProcessor;
        _logger = logger;
        _cacheFolderPath = !string.IsNullOrWhiteSpace(cacheFolderPath)
            ? cacheFolderPath
            : Plugin.Instance?.PluginCachePath ?? Path.Join(AppContext.BaseDirectory, "Cache");

        if (!Directory.Exists(_cacheFolderPath))
        {
            Directory.CreateDirectory(_cacheFolderPath);
        }
    }

    /// <summary>
    /// Gets the cache folder path.
    /// </summary>
    public string CacheFolderPath => _cacheFolderPath;

    /// <summary>
    /// Gets an existing subset subtitle file or generates one on-demand (JIT).
    /// </summary>
    /// <param name="itemId">The media item identifier.</param>
    /// <param name="originalAssPath">The physical path of the original ASS file.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The path to the final ASS file (cached or original as fallback).</returns>
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Paths are guided by sanitized database-derived item IDs and hardcoded directories.")]
    public virtual async Task<string> GetOrGenerateSubtitleAsync(Guid itemId, string originalAssPath, CancellationToken cancellationToken = default)
    {
        string safeFileName = Path.GetFileName(originalAssPath);
        string cacheFilePath = Path.Join(_cacheFolderPath, $"{itemId:N}_{safeFileName}");

        if (File.Exists(cacheFilePath))
        {
            _logger.LogInformation("[AssSubsetter] Cache hit: Returning existing subsetted subtitle for item {ItemId}", itemId);
            try
            {
                File.SetLastAccessTime(cacheFilePath, DateTime.Now);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Failed to update last access time for cache file: {Path} (IO Error)", cacheFilePath);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Failed to update last access time for cache file: {Path} (Permission Error)", cacheFilePath);
            }

            return cacheFilePath;
        }

        var fileLock = _fileLocks.GetOrAdd(itemId, _ => new SemaphoreSlim(1, 1));
        await fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (File.Exists(cacheFilePath))
            {
                _logger.LogInformation("[AssSubsetter] Cache hit (after lock): Returning existing subsetted subtitle for item {ItemId}", itemId);
                try
                {
                    File.SetLastAccessTime(cacheFilePath, DateTime.Now);
                }
                catch (IOException)
                {
                    /* 忽略 */
                }
                catch (UnauthorizedAccessException)
                {
                    /* 忽略 */
                }

                return cacheFilePath;
            }

            try
            {
                long requiredSpace = File.Exists(originalAssPath) ? new FileInfo(originalAssPath).Length : 2 * 1024 * 1024;
                EnforceCapacityLimit(requiredSpace);
            }
            catch (IOException ex)
            {
                _logger.LogError(ex, "IO Error occurred during LRU cache eviction.");
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogError(ex, "Permission Error occurred during LRU cache eviction.");
            }

            _logger.LogInformation("[AssSubsetter] Cache miss for item {ItemId}. Triggering on-demand JIT subsetting...", itemId);
            bool success = await _assProcessor.GenerateSubsetFontAsync(originalAssPath, cacheFilePath, cancellationToken).ConfigureAwait(false);

            if (success && File.Exists(cacheFilePath))
            {
                try
                {
                    File.SetLastAccessTime(cacheFilePath, DateTime.Now);
                }
                catch (IOException)
                {
                    /* 忽略 */
                }
                catch (UnauthorizedAccessException)
                {
                    /* 忽略 */
                }

                return cacheFilePath;
            }

            _logger.LogWarning("[AssSubsetter] JIT subsetting failed for item {ItemId}. Falling back.", itemId);
            return _config.FallbackToOriginalOnError ? originalAssPath : string.Empty;
        }
        finally
        {
            fileLock.Release();
        }
    }

    private void EnforceCapacityLimit(long requiredSpace)
    {
        var dirInfo = new DirectoryInfo(_cacheFolderPath);
        long maxCacheSizeInBytes = (long)_config.MaxCacheSizeMB * 1024 * 1024;
        long currentSize = dirInfo.EnumerateFiles("*.ass").Sum(f => f.Length);

        if (currentSize + requiredSpace <= maxCacheSizeInBytes)
        {
            return;
        }

        _logger.LogInformation("[AssSubsetter] Cache folder quota exceeded ({Current}MB / {Max}MB). Running LRU eviction...", currentSize / 1024 / 1024, _config.MaxCacheSizeMB);

        var oldestFiles = dirInfo.GetFiles("*.ass")
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
                _logger.LogWarning(ex, "Failed to delete evicted cache file: {Name} (IO Error)", file.Name);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Failed to delete evicted cache file: {Name} (Permission Error)", file.Name);
            }
        }
    }
}
