using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Configuration;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Jellyfin.Plugin.AssSubsetter.Services;

/// <summary>
/// Manages the discovery and indexing of physical font files on the system.
/// </summary>
public sealed class FontCacheManager : IDisposable
{
    private static readonly JsonSerializerOptions _jsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly char[] _splitChars = new[] { ';', ',' };
    private readonly ILogger<FontCacheManager> _logger;
    private readonly string _cacheFilePath;
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly PluginConfiguration _config;

    private Dictionary<string, List<FontCacheEntry>> _fontIndex = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _isLoaded;

    /// <summary>
    /// Initializes a new instance of the <see cref="FontCacheManager"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="config">The plugin configuration.</param>
    public FontCacheManager(ILogger<FontCacheManager> logger, PluginConfiguration config)
    {
        _logger = logger;
        _config = config;

        _cacheFilePath = string.IsNullOrWhiteSpace(_config.FontCacheFilePath)
            ? Path.Join(Plugin.Instance?.PluginDataPath ?? AppContext.BaseDirectory, "font_caches.json")
            : _config.FontCacheFilePath;

        var cacheDir = Path.GetDirectoryName(_cacheFilePath);
        if (!string.IsNullOrEmpty(cacheDir) && !Directory.Exists(cacheDir))
        {
            Directory.CreateDirectory(cacheDir);
        }
    }

    /// <summary>
    /// Gets the list of directories to scan for fonts.
    /// </summary>
    private List<string> GetFontDirectories()
    {
        var dirs = new List<string>();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            dirs.Add(Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts"));
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            dirs.Add("/usr/share/fonts");
            dirs.Add("/usr/local/share/fonts");
            dirs.Add(Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local/share/fonts"));
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            dirs.Add("/System/Library/Fonts");
            dirs.Add("/Library/Fonts");
            dirs.Add(Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Fonts"));
        }

        if (!string.IsNullOrWhiteSpace(_config.CustomFontDirectories))
        {
            var customDirs = _config.CustomFontDirectories.Split(_splitChars, StringSplitOptions.RemoveEmptyEntries);
            foreach (var dir in customDirs)
            {
                string trimmedDir = dir.Trim();
                if (Directory.Exists(trimmedDir))
                {
                    dirs.Add(trimmedDir);
                }
                else
                {
                    _logger.LogWarning("[AssSubsetter] Custom font directory does not exist or is inaccessible: {Dir}", trimmedDir);
                }
            }
        }

        return dirs.Where(Directory.Exists).Distinct().ToList();
    }

    /// <summary>
    /// Scans directories and updates the JSON cache.
    /// </summary>
    /// <param name="progress">The progress reporter.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task ScanAndSaveAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        await _scanLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _logger.LogInformation("[AssSubsetter] Starting font cache scan...");
            progress?.Report(0);

            var existingCache = new Dictionary<string, List<FontCacheEntry>>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(_cacheFilePath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(_cacheFilePath, cancellationToken).ConfigureAwait(false);
                    var cachedList = JsonSerializer.Deserialize<List<FontCacheEntry>>(json);
                    if (cachedList != null)
                    {
                        foreach (var entry in cachedList)
                        {
                            if (!existingCache.TryGetValue(entry.Path, out var list))
                            {
                                list = new List<FontCacheEntry>();
                                existingCache[entry.Path] = list;
                            }

                            list.Add(entry);
                        }
                    }
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "[AssSubsetter] Failed to parse existing font cache JSON. A full rebuild will be performed.");
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "[AssSubsetter] IO Error reading existing font cache. A full rebuild will be performed.");
                }
            }

            var directories = GetFontDirectories();
            var allFontFiles = new List<string>();

            foreach (var dir in directories)
            {
                try
                {
                    allFontFiles.AddRange(Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories)
                        .Where(f => f.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase) ||
                                    f.EndsWith(".otf", StringComparison.OrdinalIgnoreCase) ||
                                    f.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase)));
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "[AssSubsetter] IO Error scanning directory {Dir}", dir);
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.LogWarning(ex, "[AssSubsetter] Permission Error scanning directory {Dir}", dir);
                }
            }

            var updatedCacheList = new ConcurrentBag<FontCacheEntry>();
            int totalFiles = allFontFiles.Count;
            int processed = 0;

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = cancellationToken
            };

            Parallel.ForEach(allFontFiles, parallelOptions, file =>
            {
                try
                {
                    var fileInfo = new FileInfo(file);

                    if (existingCache.TryGetValue(file, out var existingEntries) &&
                        existingEntries.Count > 0 &&
                        existingEntries[0].LastWriteTimeUtc == fileInfo.LastWriteTimeUtc &&
                        existingEntries.Any(e => !string.IsNullOrEmpty(e.Style) || e.FaceIndex > 0 || !file.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase)))
                    {
                        // Unchanged and has new features
                        foreach (var entry in existingEntries)
                        {
                            updatedCacheList.Add(entry);
                        }
                    }
                    else
                    {
                        // Determine face count
                        int faceCount = 1;
                        if (file.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                using var fs = File.OpenRead(file);
                                byte[] header = new byte[12];
                                if (fs.Read(header, 0, 12) == 12 && header[0] == 't' && header[1] == 't' && header[2] == 'c' && header[3] == 'f')
                                {
                                    faceCount = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4));
                                    if (faceCount <= 0 || faceCount > 100)
                                    {
                                        faceCount = 1;
                                    }
                                }
                            }
                            catch (IOException ex)
                            {
                                _logger.LogDebug(ex, "[AssSubsetter] IO Error reading TTC header for file {File}", file);
                            }
                            catch (UnauthorizedAccessException ex)
                            {
                                _logger.LogDebug(ex, "[AssSubsetter] Permission Error reading TTC header for file {File}", file);
                            }
                        }

                        for (int faceIndex = 0; faceIndex < faceCount; faceIndex++)
                        {
                            // Need to read metadata
                            using var typeface = SKTypeface.FromFile(file, faceIndex);
                            if (typeface != null)
                            {
                                var extracted = ExtractFontNames(typeface);
                                var names = extracted.Names;
                                if (names.Count == 0 && !string.IsNullOrEmpty(typeface.FamilyName))
                                {
                                    names.Add(typeface.FamilyName);
                                }

                                if (names.Count > 0)
                                {
                                    updatedCacheList.Add(new FontCacheEntry
                                    {
                                        Path = file,
                                        FaceIndex = faceIndex,
                                        Type = System.IO.Path.GetExtension(file).TrimStart('.').ToLowerInvariant(),
                                        FamilyName = names.First(),
                                        Style = extracted.Style,
                                        Names = names,
                                        Weight = typeface.FontWeight,
                                        Width = typeface.FontWidth,
                                        IsItalic = typeface.FontSlant != SKFontStyleSlant.Upright,
                                        IsFixedPitch = typeface.IsFixedPitch,
                                        LastWriteTimeUtc = fileInfo.LastWriteTimeUtc
                                    });
                                }
                            }
                        }
                    }
                }
                catch (IOException)
                {
                    // Ignore unreadable fonts
                }
                catch (UnauthorizedAccessException)
                {
                    // Ignore unreadable fonts
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[AssSubsetter] Unexpected error reading font metadata for file {File}", file);
                }

                int current = Interlocked.Increment(ref processed);
                if (current % 100 == 0 || current == totalFiles)
                {
                    progress?.Report((double)current / totalFiles * 100);
                }
            });

            var finalList = updatedCacheList.ToList();

            _fontIndex = BuildIndex(finalList);
            _isLoaded = true;

            // Save
            var jsonOutput = JsonSerializer.Serialize(finalList, _jsonOptions);
            await File.WriteAllTextAsync(_cacheFilePath, jsonOutput, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("[AssSubsetter] Font cache scan completed. Indexed {Count} fonts.", finalList.Count);
            progress?.Report(100);
        }
        finally
        {
            _scanLock.Release();
        }
    }

    /// <summary>
    /// Loads the cache into memory if not already loaded. Triggers a full scan if no cache file exists.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_isLoaded)
        {
            return;
        }

        bool needsScan = false;

        await _scanLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isLoaded)
            {
                return;
            }

            if (File.Exists(_cacheFilePath))
            {
                var json = await File.ReadAllTextAsync(_cacheFilePath, cancellationToken).ConfigureAwait(false);
                var cachedList = JsonSerializer.Deserialize<List<FontCacheEntry>>(json);
                if (cachedList != null)
                {
                    _fontIndex = BuildIndex(cachedList);
                }

                _isLoaded = true;
            }
            else
            {
                needsScan = true;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "[AssSubsetter] Failed to parse font cache JSON from disk.");
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "[AssSubsetter] IO error loading font cache from disk.");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "[AssSubsetter] Permission error loading font cache from disk.");
        }
        finally
        {
            _scanLock.Release();
        }

        if (needsScan)
        {
            await ScanAndSaveAsync(null, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Finds the best matching physical font file for the given font name and styles.
    /// </summary>
    /// <param name="desc">The font variant descriptor containing name, weight, and italic properties.</param>
    /// <returns>A tuple containing the path and face index, or null if not found.</returns>
    public (string Path, int FaceIndex)? FindFontFilePath(FontDescriptor desc)
    {
        if (!_isLoaded)
        {
            EnsureLoadedAsync().GetAwaiter().GetResult();
        }

        string fontName = desc.FontName;

        // Try exact match on FamilyName or any of its extracted Names
        if (_fontIndex.TryGetValue(fontName, out var entries) && entries.Count > 0)
        {
            var best = FindBestVariant(entries, desc);
            return (best.Path, best.FaceIndex);
        }

        // Try loose match (Contains)
        foreach (var kvp in _fontIndex)
        {
            if (kvp.Key.Contains(fontName, StringComparison.OrdinalIgnoreCase) ||
                fontName.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
            {
                var best = FindBestVariant(kvp.Value, desc);
                return (best.Path, best.FaceIndex);
            }
        }

        return null;
    }

    private static FontCacheEntry FindBestVariant(List<FontCacheEntry> entries, FontDescriptor desc)
    {
        if (entries.Count == 1)
        {
            return entries[0];
        }

        // Filter by Italic if there's an exact match for the italic preference
        var italicMatches = entries.Where(e => e.IsItalic == desc.IsItalic).ToList();
        var candidates = italicMatches.Count > 0 ? italicMatches : entries;

        // Weight fallback logic
        if (desc.RequestedWeight.HasValue)
        {
            return candidates.OrderBy(e => Math.Abs(e.Weight - desc.RequestedWeight.Value)).First();
        }

        if (desc.IsBoldRequest)
        {
            // Pick maximum weight available
            return candidates.OrderByDescending(e => e.Weight).First();
        }

        // Default to closest to Regular (400)
        return candidates.OrderBy(e => Math.Abs(e.Weight - 400)).First();
    }

    private static (List<string> Names, string Style) ExtractFontNames(SKTypeface typeface)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string style = string.Empty;

        uint nameTag = 1851878757; // 'n'<<24 | 'a'<<16 | 'm'<<8 | 'e'
        byte[]? data = typeface.GetTableData(nameTag);

        if (data != null && data.Length >= 6)
        {
            ushort count = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(2));
            ushort stringOffset = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4));

            for (int i = 0; i < count; i++)
            {
                int recordOffset = 6 + (i * 12);
                if (recordOffset + 12 > data.Length)
                {
                    break;
                }

                ushort platformId = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(recordOffset));
                ushort encodingId = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(recordOffset + 2));
                ushort nameId = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(recordOffset + 6));
                ushort length = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(recordOffset + 8));
                ushort offset = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(recordOffset + 10));

                // 1 = Font Family name, 2 = Font Subfamily name (Style), 4 = Full font name, 6 = PostScript name, 16 = Typographic Family name
                if (nameId == 1 || nameId == 2 || nameId == 4 || nameId == 6 || nameId == 16)
                {
                    int stringStart = stringOffset + offset;
                    if (stringStart + length <= data.Length)
                    {
                        string str = string.Empty;
                        try
                        {
                            if (platformId == 3 && (encodingId == 1 || encodingId == 10))
                            {
                                // Windows Unicode
                                str = System.Text.Encoding.BigEndianUnicode.GetString(data, stringStart, length);
                            }
                            else if (platformId == 1 && encodingId == 0)
                            {
                                // Mac Roman
                                str = System.Text.Encoding.UTF8.GetString(data, stringStart, length);
                            }
                        }
                        catch (ArgumentException)
                        {
                            // Ignore decoding errors
                        }

                        if (!string.IsNullOrWhiteSpace(str) && !str.Contains('?', StringComparison.Ordinal) && str.All(c => !char.IsControl(c)))
                        {
                            if (nameId == 2 && string.IsNullOrEmpty(style))
                            {
                                style = str.Trim();
                            }
                            else if (nameId == 1 || nameId == 4 || nameId == 6 || nameId == 16)
                            {
                                names.Add(str.Trim());
                            }
                        }
                    }
                }
            }
        }

        return (names.ToList(), style);
    }

    private static Dictionary<string, List<FontCacheEntry>> BuildIndex(IEnumerable<FontCacheEntry> entries)
    {
        var index = new Dictionary<string, List<FontCacheEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            var namesToMap = entry.Names != null && entry.Names.Count > 0
                ? entry.Names
                : new List<string> { entry.FamilyName };

            foreach (var name in namesToMap)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                if (!index.TryGetValue(name, out var list))
                {
                    list = new List<FontCacheEntry>();
                    index[name] = list;
                }

                list.Add(entry);
            }
        }

        return index;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _scanLock.Dispose();
    }
}
