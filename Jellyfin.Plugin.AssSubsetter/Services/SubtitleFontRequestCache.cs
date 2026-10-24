using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.AssSubsetter.Core;
using Jellyfin.Plugin.AssSubsetter.Models;

namespace Jellyfin.Plugin.AssSubsetter.Services;

/// <summary>Bounds retained source parsing by both entry count and character count.</summary>
internal sealed class SubtitleFontRequestCache
{
    private const int MaximumEntries = 128;
    private const int MaximumCharacters = 262144;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private int _characters;

    internal IReadOnlyList<KeyValuePair<FontDescriptor, uint[]>> Get(string path)
    {
        path = Path.GetFullPath(path);
        var source = new FileInfo(path);
        long length = source.Exists ? source.Length : -1;
        long modified = source.Exists ? source.LastWriteTimeUtc.Ticks : 0;
        lock (_gate)
        {
            if (_entries.TryGetValue(path, out Entry? cached) && cached.Length == length && cached.Modified == modified)
            {
                return cached.Requests;
            }
        }

        KeyValuePair<FontDescriptor, uint[]>[] requests = length < 0 ? [] : new AssDocumentParser().ExtractUsedCharacters(path)
            .Select(pair => new KeyValuePair<FontDescriptor, uint[]>(pair.Key, pair.Value.Order().ToArray())).ToArray();
        source.Refresh();
        if ((source.Exists ? source.Length : -1) != length || (source.Exists ? source.LastWriteTimeUtc.Ticks : 0) != modified)
        {
            throw new IOException("Subtitle changed while parsing its font dependencies.");
        }

        int count = requests.Sum(pair => pair.Value.Length);
        lock (_gate)
        {
            if (_entries.Remove(path, out Entry? previous))
            {
                _characters -= previous.Characters;
            }

            if (count <= MaximumCharacters)
            {
                while (_entries.Count >= MaximumEntries || _characters + count > MaximumCharacters)
                {
                    string oldest = _entries.Keys.First();
                    _characters -= _entries[oldest].Characters;
                    _entries.Remove(oldest);
                }

                _entries[path] = new Entry(length, modified, requests, count);
                _characters += count;
            }
        }

        return requests;
    }

    private sealed record Entry(long Length, long Modified, IReadOnlyList<KeyValuePair<FontDescriptor, uint[]>> Requests, int Characters);
}
