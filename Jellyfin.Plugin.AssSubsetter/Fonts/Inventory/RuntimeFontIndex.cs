using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Models;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

/// <summary>
/// Immutable, repository-independent lookup snapshot used by subtitle processing.
/// </summary>
public sealed class RuntimeFontIndex
{
    private const int MaximumLooseCandidates = 256;
    private readonly ImmutableDictionary<string, ImmutableArray<RuntimeFontFace>> _familyExact;
    private readonly ImmutableDictionary<string, ImmutableArray<RuntimeFontFace>> _exact;
    private readonly ImmutableDictionary<string, ImmutableArray<RuntimeFontFace>> _tokens;

    /// <summary>Initializes a new instance of the <see cref="RuntimeFontIndex"/> class from a complete snapshot.</summary>
    /// <param name="faces">The published runtime Faces to index.</param>
    public RuntimeFontIndex(IEnumerable<RuntimeFontFace> faces)
    {
        RuntimeFontFace[] snapshot = faces.ToArray();
        _familyExact = BuildLookup(snapshot, face => EnumerateFamilyAliases(face).Select(Normalize));
        _exact = BuildLookup(snapshot, face => EnumerateAliases(face).Select(Normalize));
        _tokens = BuildLookup(snapshot, face => EnumerateAliases(face).SelectMany(Tokenize).Distinct(StringComparer.Ordinal));
    }

    /// <summary>Gets an empty runtime font index.</summary>
    public static RuntimeFontIndex Empty { get; } = new([]);

    /// <summary>Finds the best published Face without accessing persistent storage.</summary>
    /// <param name="descriptor">The requested ASS family and style attributes.</param>
    /// <param name="canSelect">An optional memory-only maintenance predicate that excludes invalidated candidates.</param>
    /// <param name="requiredCharacters">Characters used by the current subtitle style, when known.</param>
    /// <returns>The best Face, or <see langword="null"/> when no bounded candidate exists.</returns>
    public RuntimeFontFace? Find(
        FontDescriptor descriptor,
        Func<RuntimeFontFace, bool>? canSelect = null,
        IReadOnlyCollection<uint>? requiredCharacters = null)
    {
        string normalized = Normalize(descriptor.FontName);
        if (_familyExact.TryGetValue(normalized, out ImmutableArray<RuntimeFontFace> family))
        {
            RuntimeFontFace? bestFamily = SelectBest(family, descriptor, canSelect, requiredCharacters);
            if (bestFamily is not null)
            {
                return bestFamily;
            }
        }

        if (_exact.TryGetValue(normalized, out ImmutableArray<RuntimeFontFace> exact))
        {
            RuntimeFontFace? bestExact = SelectBest(exact, descriptor, canSelect, requiredCharacters);
            if (bestExact is not null)
            {
                return bestExact;
            }
        }

        string[] tokens = Tokenize(normalized).ToArray();
        if (tokens.Length == 0)
        {
            return null;
        }

        IEnumerable<RuntimeFontFace> candidates = tokens
            .Where(_tokens.ContainsKey)
            .SelectMany(token => _tokens[token])
            .DistinctBy(face => face.FaceId)
            .Where(face => EnumerateAliases(face).Any(alias =>
                Normalize(alias).Contains(normalized, StringComparison.Ordinal)
                || normalized.Contains(Normalize(alias), StringComparison.Ordinal)))
            .OrderBy(face => EnumerateAliases(face).Min(alias => Math.Abs(Normalize(alias).Length - normalized.Length)))
            .ThenBy(face => face.Path, StringComparer.Ordinal)
            .Take(MaximumLooseCandidates);
        return SelectBest(candidates, descriptor, canSelect, requiredCharacters);
    }

    private static RuntimeFontFace? SelectBest(
        IEnumerable<RuntimeFontFace> faces,
        FontDescriptor descriptor,
        Func<RuntimeFontFace, bool>? canSelect,
        IReadOnlyCollection<uint>? requiredCharacters)
    {
        // Exact aliases commonly resolve to a single published face.  Avoid allocating
        // and sorting a one-element sequence on the subtitle hot path.
        if (faces is ImmutableArray<RuntimeFontFace> { Length: 1 } exactFace)
        {
            return canSelect is null || canSelect(exactFace[0]) ? exactFace[0] : null;
        }

        RuntimeFontFace[] eligible = faces.Where(face => canSelect is null || canSelect(face)).ToArray();
        if (requiredCharacters is { Count: > 0 })
        {
            RuntimeFontFace[] complete = eligible
                .Where(face => face.UnicodeCoverage.Count > 0 && requiredCharacters.All(character => Covers(face.UnicodeCoverage, character)))
                .ToArray();
            if (complete.Length > 0)
            {
                eligible = complete;
            }
        }

        int requestedWeight = descriptor.RequestedWeight ?? (descriptor.IsBoldRequest ? 700 : 400);
        return eligible
            .OrderBy(face => face.IsItalic == descriptor.IsItalic ? 0 : 1)
            .ThenBy(face => Math.Abs(face.Weight - requestedWeight))
            .ThenBy(face => Math.Abs(face.Width - 5))
            .ThenBy(face => StylePenalty(face, descriptor, requestedWeight))
            .ThenByDescending(face => face.RetentionScore)
            .ThenByDescending(face => face.IsRuntimePreferred)
            .ThenBy(face => face.Path, StringComparer.Ordinal)
            .ThenBy(face => face.FaceIndex)
            .FirstOrDefault();
    }

    private static bool Covers(IReadOnlyList<UnicodeRange> ranges, uint character)
    {
        int low = 0;
        int high = ranges.Count - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) / 2);
            UnicodeRange range = ranges[middle];
            if (character < range.Start)
            {
                high = middle - 1;
            }
            else if (character > range.End)
            {
                low = middle + 1;
            }
            else
            {
                return true;
            }
        }

        return false;
    }

    private static int StylePenalty(RuntimeFontFace face, FontDescriptor descriptor, int requestedWeight)
    {
        string expected = requestedWeight >= 600
            ? (descriptor.IsItalic ? "BOLD ITALIC" : "BOLD")
            : (descriptor.IsItalic ? "ITALIC" : "REGULAR");
        string actual = Normalize(face.StyleName);
        if (actual == expected)
        {
            return 0;
        }

        return descriptor.IsItalic && actual.Contains("ITALIC", StringComparison.Ordinal) ? 1 : 2;
    }

    private static ImmutableDictionary<string, ImmutableArray<RuntimeFontFace>> BuildLookup(
        IReadOnlyList<RuntimeFontFace> faces,
        Func<RuntimeFontFace, IEnumerable<string>> keySelector)
    {
        return faces
            .SelectMany(face => keySelector(face).Where(key => key.Length > 0).Distinct(StringComparer.Ordinal).Select(key => (Key: key, Face: face)))
            .GroupBy(item => item.Key, StringComparer.Ordinal)
            .ToImmutableDictionary(group => group.Key, group => group.Select(item => item.Face).ToImmutableArray(), StringComparer.Ordinal);
    }

    private static IEnumerable<string> EnumerateAliases(RuntimeFontFace face)
    {
        yield return face.FamilyName;
        foreach (string name in face.Names)
        {
            yield return name;
        }

        foreach (OpenTypeNameRecord record in face.NameRecords.Where(record => record.NameId is 21))
        {
            yield return record.Value;
        }
    }

    private static IEnumerable<string> EnumerateFamilyAliases(RuntimeFontFace face)
    {
        yield return face.FamilyName;
        foreach (OpenTypeNameRecord record in face.NameRecords.Where(record => record.NameId is 1 or 16 or 21))
        {
            yield return record.Value;
        }
    }

    private static IEnumerable<string> Tokenize(string value) => Normalize(value).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static string Normalize(string value)
    {
        string normalizedValue = value.IsNormalized(NormalizationForm.FormKC)
            ? value
            : value.Normalize(NormalizationForm.FormKC);
        var result = new StringBuilder(normalizedValue.Length);
        bool pendingSpace = false;
        foreach (char character in normalizedValue)
        {
            if (char.IsWhiteSpace(character) || character is '_' or '-')
            {
                pendingSpace = result.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                result.Append(' ');
                pendingSpace = false;
            }

            result.Append(char.ToUpperInvariant(character));
        }

        return result.ToString();
    }
}
