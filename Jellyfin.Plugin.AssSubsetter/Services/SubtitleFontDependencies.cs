using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.AssSubsetter.Models;

namespace Jellyfin.Plugin.AssSubsetter.Services;

/// <summary>Immutable identities for the exact font requests in one source subtitle.</summary>
internal sealed class SubtitleFontDependencies
{
    internal SubtitleFontDependencies(IReadOnlyDictionary<string, string> selections)
    {
        Selections = selections;
        // Preserve the empty identity for sources without font usage.
        Fingerprint = selections.Count == 0 ? string.Empty : Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(string.Join("\n", selections.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => pair.Key + ":" + pair.Value)))));
    }

    internal IReadOnlyDictionary<string, string> Selections { get; }

    internal string Fingerprint { get; }

    internal static string RequestKey(FontDescriptor descriptor, IReadOnlyCollection<uint> characters)
    {
        string name = Convert.ToBase64String(Encoding.UTF8.GetBytes(descriptor.FontName.ToUpperInvariant()));
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{name}:{descriptor.RequestedWeight}:{descriptor.IsBoldRequest}:{descriptor.IsItalic}:{string.Join(",", characters.Order())}");
    }

    internal static string FaceIdentity(string hash, int faceIndex)
        => string.Create(CultureInfo.InvariantCulture, $"{hash.ToUpperInvariant()}:{faceIndex}");
}
