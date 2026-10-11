using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance.Naming;

internal static class FontNamingFieldResolver
{
    private static readonly IReadOnlyDictionary<string, ushort> NameIds = new Dictionary<string, ushort>(StringComparer.Ordinal)
    {
        ["fullName"] = 4,
        ["postscript"] = 6,
        ["manufacturer"] = 8,
        ["legacyFamily"] = 1,
        ["legacyStyle"] = 2,
        ["typographicFamily"] = 16,
        ["typographicStyle"] = 17
    };

    internal static FontNamingFields Resolve(FontFileRecord file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var selections = new List<FontNamingSelectionEvidence>();
        var faces = file.Faces.OrderBy(face => face.FaceIndex).Select(face => ReadFace(face, selections)).ToArray();
        var values = new Dictionary<string, FontNamingFieldValue>(StringComparer.Ordinal);
        foreach (var field in NameIds)
        {
            bool optional = FontNamingFieldCatalog.Fields.Single(descriptor => descriptor.Name == field.Key).MissingBehavior == "empty";
            values.Add(field.Key, Aggregate(field.Key, faces.Select(face => face.Raw[field.Key]), optional));
        }

        values.Add("family", Aggregate("family", faces.Select(face => face.Family), false, join: true));
        values.Add("style", Aggregate("style", faces.Select(face => face.Style), true));
        values.Add("version", Aggregate("version", faces.Select(face => ReadVersion(face.Face)), true));
        values.Add("styleSuffix", Suffix("styleSuffix", values["style"], "-"));
        values.Add("versionSuffix", Suffix("versionSuffix", values["version"], "-v"));

        bool collection = faces.Length > 1 || Path.GetExtension(file.Path).Equals(".ttc", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(file.Path).Equals(".otc", StringComparison.OrdinalIgnoreCase);
        var family = values["family"];
        int families = faces.Select(face => face.Family.Text).Distinct(StringComparer.Ordinal).Count();
        string? collectionReason = collection ? family.MissingReason : null;
        string collectionText = !collection || collectionReason is not null ? string.Empty : families > 1 ? "-MixedCollection" : "-Collection";
        values.Add("collectionSuffix", FileValue("collectionSuffix", collectionText, collectionReason, collectionReason is not null, "program-suffix", collection ? family.Evidence : []));
        if (collection)
        {
            selections.Add(new(null, "collection-summary", "Faces=" + faces.Length.ToString(CultureInfo.InvariantCulture) + "; families=" + (collectionReason is null ? families.ToString(CultureInfo.InvariantCulture) : "unavailable")));
        }

        values.Add("originalName", TextValue("originalName", Path.GetFileNameWithoutExtension(file.Path)));
        values.Add("faceCount", FileValue("faceCount", faces.Length == 0 ? string.Empty : faces.Length.ToString(CultureInfo.InvariantCulture), faces.Length == 0 ? "no-faces" : null, faces.Length == 0, "inventory-file", []));
        string? fingerprintReason = faces.Length == 0 ? "no-faces" : faces.Any(face => face.Face.StrictFingerprint.Length != 32) ? "invalid-fingerprint" : null;
        string fingerprint = string.Empty;
        if (fingerprintReason is null)
        {
            byte[] digest = faces.Length == 1 ? faces[0].Face.StrictFingerprint.ToArray() : SHA256.HashData(faces.SelectMany(face => face.Face.StrictFingerprint.ToArray()).ToArray());
            fingerprint = Convert.ToHexString(digest.AsSpan(0, 4)).ToLowerInvariant();
        }

        values.Add("fingerprint", FileValue("fingerprint", fingerprint, fingerprintReason, fingerprintReason is not null, "canonical-fingerprint", []));
        return new(new ReadOnlyDictionary<string, FontNamingFieldValue>(values), Array.AsReadOnly(selections.ToArray()));
    }

    internal static string? NormalizeVersion(string? version)
    {
        Match match = Regex.Match(version ?? string.Empty, @"(?:^|Version\s+)([0-9]+(?:\.[0-9]+)+)(?:$|[;\s])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static FaceNames ReadFace(FontFaceRecord face, List<FontNamingSelectionEvidence> selections)
    {
        var raw = NameIds.ToDictionary(pair => pair.Key, pair => ReadName(face, pair.Value), StringComparer.Ordinal);
        FaceValue family;
        FaceValue style;
        string kind;
        if (raw["typographicFamily"].Reason is null && raw["typographicStyle"].Reason is null)
        {
            family = raw["typographicFamily"];
            style = raw["typographicStyle"];
            kind = "typographic-pair";
        }
        else if (raw["legacyFamily"].Reason is null && raw["legacyStyle"].Reason is null)
        {
            family = raw["legacyFamily"];
            style = raw["legacyStyle"];
            kind = "legacy-pair";
        }
        else
        {
            bool typed = raw["typographicFamily"].Reason is null || (raw["legacyFamily"].Reason is not null && raw["typographicFamily"].Reason != "missing-field");
            family = raw[typed ? "typographicFamily" : "legacyFamily"];
            style = raw[typed ? "typographicStyle" : "legacyStyle"];
            if (family.Reason is not null)
            {
                style = style with { Text = string.Empty, Reason = "missing-family" };
            }

            kind = "family-only";
        }

        string? detail = kind == "typographic-pair" ? null : "typographic=" + (raw["typographicFamily"].Reason ?? raw["typographicStyle"].Reason ?? "available") + "; style=" + (style.Reason ?? "available");
        selections.Add(new(face.FaceIndex, kind, detail));
        return new(face, raw, family, style);
    }

    private static FaceValue ReadName(FontFaceRecord face, ushort nameId)
    {
        var records = face.NameRecords.Where(record => record.NameId == nameId).ToArray();
        string? value = OpenTypeNameSelector.SelectPreferred(records);
        string? reason = value is null ? records.Length == 0 ? "missing-field" : "blank-field" : null;
        string text = string.Empty;
        if (reason is null)
        {
            try
            {
                text = FontNamingComponent.Normalize(value!);
                if (text.Length == 0)
                {
                    reason = "sanitized-empty";
                }
            }
            catch (NamingEvaluationException exception)
            {
                reason = exception.Message;
            }
        }

        return new(text, nameId, face.FaceIndex, reason);
    }

    private static FaceValue ReadVersion(FontFaceRecord face)
    {
        string? reason = face.Version is null ? "missing-field" : string.IsNullOrWhiteSpace(face.Version) ? "blank-field" : null;
        string text = string.Empty;
        if (reason is null)
        {
            try
            {
                _ = FontNamingComponent.Normalize(face.Version!);
                string? parsed = NormalizeVersion(face.Version);
                reason = parsed is null ? "unreliable-version" : null;
                text = parsed ?? string.Empty;
            }
            catch (NamingEvaluationException exception)
            {
                reason = exception.Message;
            }
        }

        return new(text, null, face.FaceIndex, reason);
    }

    private static FontNamingFieldValue Aggregate(string name, IEnumerable<FaceValue> source, bool optional, bool join = false)
    {
        var inputs = source.ToArray();
        string? reason = inputs.Length == 0 ? "no-faces" : inputs.FirstOrDefault(value => value.Reason is not null)?.Reason;
        string[] distinct = inputs.Select(value => value.Text).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (reason is null && !join && distinct.Length != 1)
        {
            reason = "inconsistent-face-field";
        }

        string text = reason is null ? join ? string.Join('+', distinct) : distinct[0] : string.Empty;
        var evidence = inputs.Select(value => new FontNamingFieldEvidence(name, value.NameId, value.FaceIndex, "inventory-face", value.Reason is null ? value.Text : null) { MissingReason = value.Reason }).ToList();
        if (inputs.Length != 1)
        {
            evidence.Add(new(name, null, null, "collection-summary", text) { MissingReason = reason });
        }

        return new(text, reason, !optional && reason is not null, Array.AsReadOnly(evidence.ToArray()));
    }

    private static FontNamingFieldValue Suffix(string name, FontNamingFieldValue value, string prefix)
        => FileValue(name, value.Text.Length == 0 ? string.Empty : prefix + value.Text, value.MissingReason, value.BlocksWhenReferenced, "program-suffix", value.Evidence);

    private static FontNamingFieldValue TextValue(string name, string value)
    {
        string? reason = string.IsNullOrWhiteSpace(value) ? "blank-field" : null;
        string text = string.Empty;
        if (reason is null)
        {
            try
            {
                text = FontNamingComponent.Normalize(value);
                if (text.Length == 0)
                {
                    reason = "sanitized-empty";
                }
            }
            catch (NamingEvaluationException exception)
            {
                reason = exception.Message;
            }
        }

        return FileValue(name, text, reason, reason is not null, "inventory-file", []);
    }

    private static FontNamingFieldValue FileValue(string name, string text, string? reason, bool blocks, string source, IReadOnlyList<FontNamingFieldEvidence> dependencies)
        => new(text, reason, blocks, Array.AsReadOnly(dependencies.Append(new FontNamingFieldEvidence(name, null, null, source, text) { MissingReason = reason }).ToArray()));

    private sealed record FaceValue(string Text, int? NameId, int FaceIndex, string? Reason);

    private sealed record FaceNames(FontFaceRecord Face, IReadOnlyDictionary<string, FaceValue> Raw, FaceValue Family, FaceValue Style);
}
