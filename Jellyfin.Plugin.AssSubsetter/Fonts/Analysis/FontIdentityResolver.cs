using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;

/// <summary>Resolves deterministic identity and version evidence from an inspected Face.</summary>
public static class FontIdentityResolver
{
    private static readonly (ushort Family, ushort Subfamily)[] FamilyPairs = [(16, 17), (21, 22), (1, 2)];
    private static readonly ushort[] FamilyNameIds = [1, 16, 21];
    private static readonly ushort[] SubfamilyNameIds = [2, 17, 22];
    private static readonly ushort[] IdentityNameIds = [1, 2, 3, 4, 6, 16, 17, 21, 22];

    /// <summary>Normalizes and orders identity evidence without using filesystem metadata.</summary>
    /// <param name="face">The inspected Face whose decoded records are evaluated.</param>
    /// <returns>Normalized identity and version evidence.</returns>
    public static FontIdentityEvidence Resolve(InspectedFontFace face)
    {
        ArgumentNullException.ThrowIfNull(face);
        Dictionary<ushort, string> names = face.NameRecords
            .GroupBy(record => record.NameId)
            .ToDictionary(group => group.Key, group => NormalizeValue(OpenTypeNameSelector.SelectPreferred(group) ?? string.Empty));

        string? postScriptName = GetName(names, 6);
        (ushort familyId, ushort subfamilyId, string? family, string? subfamily) = SelectFamilyPair(names);
        string? fullName = GetName(names, 4);
        string? uniqueId = GetName(names, 3);
        string? primaryIdentity = postScriptName ?? (family is null ? null : family + "\u001f" + subfamily) ?? fullName ?? uniqueId;
        var allNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (KeyValuePair<ushort, string> name in names)
        {
            if (IdentityNameIds.Contains(name.Key))
            {
                allNames.Add(name.Value);
            }
        }

        foreach (string value in face.Names.Select(NormalizeValue).Where(value => value.Length > 0))
        {
            allNames.Add(value);
        }

        var identity = new FontIdentityEvidence(
            postScriptName,
            family,
            subfamily,
            familyId,
            subfamilyId,
            fullName,
            uniqueId,
            primaryIdentity,
            allNames,
            NormalizeOptional(face.VendorId),
            face.Weight,
            face.Width,
            face.Slant,
            ResolveVersion(face, names));
        return WithNameRoles(identity, face.NameRecords);
    }

    internal static FontIdentityEvidence WithNameRoles(FontIdentityEvidence identity, IReadOnlyList<OpenTypeNameRecord> records)
    {
        return identity with
        {
            FamilyNames = NameValues(records, FamilyNameIds),
            SubfamilyNames = NameValues(records, SubfamilyNameIds)
        };
    }

    private static HashSet<string> NameValues(IReadOnlyList<OpenTypeNameRecord> records, ushort[] ids)
    {
        return records
            .Where(record => ids.Contains(record.NameId))
            .Select(record => NormalizeValue(record.Value))
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static FontVersionEvidence ResolveVersion(InspectedFontFace face, IReadOnlyDictionary<ushort, string> names)
    {
        var sources = new List<string>();
        var values = new List<decimal>();
        AddVersion(face.HeadRevision, "head.fontRevision", sources, values);
        AddVersion(GetName(names, 5), "name.5", sources, values);
        AddVersion(GetName(names, 3), "name.3", sources, values);
        if (!string.IsNullOrWhiteSpace(face.VendorId))
        {
            sources.Add("vendor");
        }

        bool hasPrimarySource = sources.Contains("head.fontRevision", StringComparer.Ordinal)
            || sources.Contains("name.5", StringComparer.Ordinal);
        bool agrees = values.Count > 0 && values.All(value => value == values[0]);
        return new FontVersionEvidence(agrees && hasPrimarySource ? values[0] : null, agrees && hasPrimarySource, sources);
    }

    private static void AddVersion(string? declaration, string source, List<string> sources, List<decimal> values)
    {
        if (string.IsNullOrWhiteSpace(declaration))
        {
            return;
        }

        sources.Add(source);
        if (TryParseVersion(declaration, out decimal value))
        {
            values.Add(value);
        }
    }

    private static bool TryParseVersion(string declaration, out decimal version)
    {
        int start = -1;
        int end = -1;
        for (int index = 0; index < declaration.Length; index++)
        {
            char character = declaration[index];
            if (char.IsAsciiDigit(character))
            {
                start = start < 0 ? index : start;
                end = index + 1;
                continue;
            }

            if (character == '.' && start >= 0 && end == index)
            {
                end = index + 1;
                continue;
            }

            if (start >= 0)
            {
                break;
            }
        }

        string candidate = start >= 0 && end > start
            ? declaration[start..end].TrimEnd('.')
            : string.Empty;
        return decimal.TryParse(candidate, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out version);
    }

    private static (ushort FamilyId, ushort SubfamilyId, string? Family, string? Subfamily) SelectFamilyPair(
        Dictionary<ushort, string> names)
    {
        foreach ((ushort familyId, ushort subfamilyId) in FamilyPairs)
        {
            if (names.TryGetValue(familyId, out string? family) && names.TryGetValue(subfamilyId, out string? subfamily))
            {
                return (familyId, subfamilyId, family, subfamily);
            }
        }

        return (0, 0, null, null);
    }

    private static string? GetName(IReadOnlyDictionary<ushort, string> names, ushort nameId)
        => names.TryGetValue(nameId, out string? value) && value.Length > 0 ? value : null;

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : NormalizeValue(value);

    internal static string NormalizeValue(string value)
    {
        string normalized = value.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
        var result = new StringBuilder(normalized.Length);
        bool pendingSpace = false;
        foreach (char character in normalized)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = result.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                result.Append(' ');
                pendingSpace = false;
            }

            result.Append(character);
        }

        return result.ToString();
    }
}
