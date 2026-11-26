using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.AssSubsetter.Core;
using Jellyfin.Plugin.AssSubsetter.Models;

namespace Jellyfin.Plugin.AssSubsetter.Services;

/// <summary>Retains parsed font requests and canonical keys within fixed size limits.</summary>
internal sealed class SubtitleFontRequestCache
{
    private const int MaximumEntries = 128;
    private const int MaximumCharacters = 262144;
    private const int MaximumKeyCharacters = 2 * 1024 * 1024;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private int _characters;
    private long _keyCharacters;

    internal IReadOnlyList<Request> Get(string path)
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

        Request[] requests = length < 0
            ? []
            : [
                .. new AssDocumentParser().ExtractUsedCharacters(path)
                    .Select(pair =>
                    {
                        uint[] characters = [.. pair.Value.Order()];
                        return new Request(pair.Key, characters, SubtitleFontDependencies.RequestKeyFromSortedCharacters(pair.Key, characters));
                    })
              ];
        source.Refresh();
        if ((source.Exists ? source.Length : -1) != length || (source.Exists ? source.LastWriteTimeUtc.Ticks : 0) != modified)
        {
            throw new IOException("Subtitle changed while parsing its font dependencies.");
        }

        int count = requests.Sum(request => request.Characters.Length);
        long keyCharacters = requests.Sum(request => (long)request.CanonicalKey.Length);
        lock (_gate)
        {
            if (_entries.Remove(path, out Entry? previous))
            {
                _characters -= previous.Characters;
                _keyCharacters -= previous.KeyCharacters;
            }

            if (count <= MaximumCharacters && keyCharacters <= MaximumKeyCharacters)
            {
                while (_entries.Count >= MaximumEntries || _characters + count > MaximumCharacters || _keyCharacters + keyCharacters > MaximumKeyCharacters)
                {
                    string oldest = _entries.Keys.First();
                    Entry removed = _entries[oldest];
                    _characters -= removed.Characters;
                    _keyCharacters -= removed.KeyCharacters;
                    _entries.Remove(oldest);
                }

                _entries[path] = new Entry(length, modified, requests, count, keyCharacters);
                _characters += count;
                _keyCharacters += keyCharacters;
            }
        }

        return requests;
    }

    /// <summary>The sorted Unicode scalars and canonical key for one font request.</summary>
    /// <param name="Descriptor">The requested font attributes.</param>
    /// <param name="Characters">The Unicode scalars in ascending order.</param>
    /// <param name="CanonicalKey">The stable cache key for the descriptor and its characters.</param>
    internal readonly record struct Request(FontDescriptor Descriptor, uint[] Characters, string CanonicalKey);

    private sealed record Entry(long Length, long Modified, IReadOnlyList<Request> Requests, int Characters, long KeyCharacters);
}
