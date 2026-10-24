using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;
using Jellyfin.Plugin.AssSubsetter.Models;

namespace Jellyfin.Plugin.AssSubsetter.Services;

/// <summary>Checks the actual bytes consumed by conversion against its cache identity.</summary>
internal sealed class SubtitleFontDependencyValidation : IDisposable
{
    private readonly SubtitleFontDependencies _expected;
    private readonly Action _restore;
    private readonly HashSet<string> _observed = new(StringComparer.Ordinal);
    private bool _valid = true;

    internal SubtitleFontDependencyValidation(SubtitleFontDependencies expected, Action restore)
    {
        _expected = expected;
        _restore = restore;
    }

    internal bool IsComplete => _valid && _observed.Count == _expected.Selections.Count;

    internal void Observe(FontDescriptor descriptor, IReadOnlyCollection<uint> characters, FontFileLease? lease, byte[]? bytes)
    {
        string key = SubtitleFontDependencies.RequestKey(descriptor, characters);
        string identity = lease is null ? "missing" : bytes is null ? "unreadable" :
            SubtitleFontDependencies.FaceIdentity(Convert.ToHexString(SHA256.HashData(bytes)), lease.FaceIndex);
        if (!_expected.Selections.TryGetValue(key, out string? expected) || !StringComparer.Ordinal.Equals(identity, expected))
        {
            _valid = false;
            throw new IOException("Font selection or content changed during subtitle conversion.");
        }

        _observed.Add(key);
    }

    /// <inheritdoc />
    public void Dispose() => _restore();
}
