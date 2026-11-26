using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.Services;

/// <summary>Tracks committed subtitle cache bytes and serializes publication with eviction.</summary>
/// <param name="directory">The subtitle cache directory.</param>
/// <param name="logger">The cache diagnostics logger.</param>
/// <param name="timeProvider">The clock used to measure the interval between scans.</param>
internal sealed class SubtitleCacheQuota(string directory, ILogger logger, TimeProvider timeProvider)
{
    private static readonly TimeSpan _recheckInterval = TimeSpan.FromSeconds(30);
    private static readonly StringComparison _pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private readonly Lock _gate = new();
    private long? _lastScanTimestamp;
    private long _currentBytes;

    internal void EnsureCapacity(long newBytes, long limit)
    {
        lock (_gate)
        {
            MakeSpace(newBytes, limit, outputPath: null);
        }
    }

    internal void Publish(string temporaryPath, string outputPath, long limit, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long sizeChange = 0;
            try
            {
                sizeChange = MakeSpace(new FileInfo(temporaryPath).Length, limit, outputPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _lastScanTimestamp = null;
                logger.LogError(exception, "[AssSubsetter] Could not check the cache quota before publishing {Path}.", outputPath);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, outputPath, overwrite: true);
            if (_lastScanTimestamp is not null)
            {
                _currentBytes += sizeChange;
            }
        }
    }

    private long MakeSpace(long newBytes, long limit, string? outputPath)
    {
        FileInfo[]? files = null;
        if (_lastScanTimestamp is not long timestamp || timeProvider.GetElapsedTime(timestamp) >= _recheckInterval || File.Exists(outputPath))
        {
            files = ScanDirectory();
        }

        long sizeChange = newBytes - GetReplacedBytes(files, outputPath);
        if (_currentBytes + sizeChange > limit)
        {
            // Refresh file sizes and access times before deleting entries.
            if (files is null)
            {
                files = ScanDirectory();
                sizeChange = newBytes - GetReplacedBytes(files, outputPath);
            }

            if (_currentBytes + sizeChange > limit)
            {
                Evict(files, sizeChange, limit, outputPath);
            }
        }

        return sizeChange;
    }

    private FileInfo[] ScanDirectory()
    {
        _lastScanTimestamp = null;
        long started = timeProvider.GetTimestamp();
        FileInfo[] files = [.. new DirectoryInfo(directory).EnumerateFiles().Where(IsManagedCacheFile)];
        _currentBytes = files.Sum(file => file.Length);
        _lastScanTimestamp = timeProvider.GetTimestamp();
        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug(
                "[AssSubsetter] Scanned subtitle cache directory. Files: {Count}, bytes: {Bytes}, elapsed ms: {ElapsedMs}.",
                files.Length,
                _currentBytes,
                timeProvider.GetElapsedTime(started, _lastScanTimestamp.Value).TotalMilliseconds);
        }

        return files;
    }

    private void Evict(FileInfo[] files, long sizeChange, long limit, string? outputPath)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("[AssSubsetter] Cache folder quota exceeded. CurrentBytes: {CurrentBytes}; LimitBytes: {LimitBytes}. Running LRU eviction.", _currentBytes, limit);
        }

        foreach (FileInfo file in files.OrderBy(file => file.LastAccessTimeUtc))
        {
            if (_currentBytes + sizeChange <= limit)
            {
                break;
            }

            if (string.Equals(outputPath, file.FullName, _pathComparison))
            {
                continue;
            }

            try
            {
                long fileSize = file.Length;
                file.Delete();
                _currentBytes -= fileSize;
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug("[AssSubsetter] LRU evicted oldest cache file: {Name}.", file.Name);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogError(exception, "[AssSubsetter] Failed to delete evicted cache file {Name}.", file.Name);
            }
        }
    }

    private static long GetReplacedBytes(FileInfo[]? files, string? outputPath)
        => outputPath is null ? 0 : files?.FirstOrDefault(file => string.Equals(outputPath, file.FullName, _pathComparison))?.Length ?? 0;

    private static bool IsManagedCacheFile(FileInfo file)
    {
        ReadOnlySpan<char> name = file.Name.AsSpan();
        ReadOnlySpan<char> extension = Path.GetExtension(name);
        return (extension.Equals(".ass", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".sup", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".mks", StringComparison.OrdinalIgnoreCase))
            && !Path.GetFileNameWithoutExtension(name).EndsWith(".partial", StringComparison.OrdinalIgnoreCase);
    }
}
