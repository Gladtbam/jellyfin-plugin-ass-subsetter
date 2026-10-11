using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;

/// <summary>Classifies a Face pair with the approved strict first-match relationship chain.</summary>
public sealed class FontRelationshipClassifier
{
    private static readonly FontSemanticGroup[] FunctionalGroups =
    [
        FontSemanticGroup.Mapping,
        FontSemanticGroup.OutlinesAndMetrics,
        FontSemanticGroup.Layout,
        FontSemanticGroup.Hinting,
        FontSemanticGroup.ColorAndBitmap,
        FontSemanticGroup.Variation,
        FontSemanticGroup.OtherFunctional
    ];

    /// <summary>Returns the first relationship proven by complete pair evidence.</summary>
    /// <param name="left">Evidence for the left candidate.</param>
    /// <param name="right">Evidence for the right candidate.</param>
    /// <returns>The relationship, evidence keys, and safe direct replacement directions.</returns>
    public FontRelationshipResult Classify(FontFaceEvidence left, FontFaceEvidence right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        if (!left.IsClassifiable || !right.IsClassifiable)
        {
            return Result(FontRelationshipKind.Unclassifiable, false, false, "classification.incomplete");
        }

        if (!string.Equals(left.PhysicalFileId, right.PhysicalFileId, StringComparison.Ordinal)
            && left.FaceIndex == right.FaceIndex
            && left.FileHash.Length > 0
            && Equal(left.FileHash, right.FileHash))
        {
            return Result(FontRelationshipKind.IdenticalFileHash, true, true, "container.sha256", "face.index");
        }

        if (left.StrictFingerprint.Length > 0 && Equal(left.StrictFingerprint, right.StrictFingerprint))
        {
            return Result(FontRelationshipKind.StrictFaceDuplicate, true, true, "face.strict-fingerprint");
        }

        if (!DeepCharacterEvidenceComplete(left) || !DeepCharacterEvidenceComplete(right))
        {
            return Result(FontRelationshipKind.Unclassifiable, false, false, "glyphs.incomplete");
        }

        if (RenderingAndLayoutEquivalent(left, right))
        {
            (bool leftReplaces, bool rightReplaces) = ReplacementDirections(left, right);
            return Result(FontRelationshipKind.RenderingAndLayoutEquivalent, leftReplaces, rightReplaces, "semantics.all");
        }

        if (MetadataOnlyDifference(left, right))
        {
            (bool leftReplaces, bool rightReplaces) = ReplacementDirections(left, right);
            return Result(FontRelationshipKind.MetadataOnlyDifference, leftReplaces, rightReplaces, "semantics.metadata");
        }

        FontRelationshipResult? superset = CoverageSuperset(left, right);
        if (superset is not null)
        {
            return superset;
        }

        if (CoverageVariant(left, right))
        {
            return Result(FontRelationshipKind.CoverageVariant, false, false, "coverage.intersection");
        }

        uint[] differingGlyphs = DifferingGlyphs(left, right);
        if (SmallGlyphDifference(left, right, differingGlyphs))
        {
            return new FontRelationshipResult(
                FontRelationshipKind.SmallGlyphDifference,
                false,
                false,
                ["glyphs.bounded-difference"],
                differingGlyphs);
        }

        if (OnlySemanticGroupDiffers(left, right, FontSemanticGroup.Layout))
        {
            return Result(FontRelationshipKind.LayoutOnlyDifference, false, false, "semantics.layout");
        }

        if (OnlySemanticGroupDiffers(left, right, FontSemanticGroup.Hinting))
        {
            return Result(FontRelationshipKind.HintingOnlyDifference, false, false, "semantics.hinting");
        }

        if (SameIdentity(left, right)
            && left.Identity.Version.IsReliable
            && right.Identity.Version.IsReliable
            && left.Identity.Version.Value.HasValue
            && right.Identity.Version.Value.HasValue
            && left.Identity.Version.Value != right.Identity.Version.Value)
        {
            return Result(FontRelationshipKind.VersionConflict, false, false, "identity.same", "version.orderable");
        }

        if (SameIdentity(left, right))
        {
            return Result(FontRelationshipKind.SameIdentityContentConflict, false, false, "identity.same", "semantics.conflict");
        }

        if (SameValue(left.Identity.PostScriptName, right.Identity.PostScriptName))
        {
            return Result(FontRelationshipKind.PostScriptNameConflict, false, false, "name.postscript");
        }

        if (HasConflictingNameReference(left.Identity, right.Identity))
        {
            return Result(FontRelationshipKind.CrossNameConflict, false, false, "name.cross-reference");
        }

        return Result(FontRelationshipKind.Independent, false, false, "identity.independent");
    }

    private static bool RenderingAndLayoutEquivalent(FontFaceEvidence left, FontFaceEvidence right)
    {
        return StructuralFieldsEqual(left, right)
            && CoverageEqual(left, right)
            && SelectorsEqual(left, right)
            && CharacterEvidenceEqual(left, right)
            && SemanticGroupsEqual(left, right, Enum.GetValues<FontSemanticGroup>());
    }

    private static bool MetadataOnlyDifference(FontFaceEvidence left, FontFaceEvidence right)
    {
        return StructuralFieldsEqual(left, right)
            && CoverageEqual(left, right)
            && SelectorsEqual(left, right)
            && CharacterEvidenceEqual(left, right)
            && SemanticGroupsEqual(left, right, FunctionalGroups)
            && !SemanticGroupEqual(left, right, FontSemanticGroup.Metadata);
    }

    private static FontRelationshipResult? CoverageSuperset(FontFaceEvidence left, FontFaceEvidence right)
    {
        if (!SameIdentity(left, right)
            || !SemanticGroupsEqual(left, right, FunctionalGroups.Where(group => group != FontSemanticGroup.Mapping))
            || !CommonCharactersEqual(left, right))
        {
            return null;
        }

        bool leftContains = right.CharacterFingerprints.Keys.All(left.CharacterFingerprints.ContainsKey)
            && left.VariationSelectors.IsSupersetOf(right.VariationSelectors)
            && VariationsContain(left, right)
            && (left.CharacterFingerprints.Count > right.CharacterFingerprints.Count
                || left.VariationSelectors.Count > right.VariationSelectors.Count
                || left.VariationFingerprints.Count > right.VariationFingerprints.Count);
        bool rightContains = left.CharacterFingerprints.Keys.All(right.CharacterFingerprints.ContainsKey)
            && right.VariationSelectors.IsSupersetOf(left.VariationSelectors)
            && VariationsContain(right, left)
            && (right.CharacterFingerprints.Count > left.CharacterFingerprints.Count
                || right.VariationSelectors.Count > left.VariationSelectors.Count
                || right.VariationFingerprints.Count > left.VariationFingerprints.Count);
        if (!leftContains && !rightContains)
        {
            return null;
        }

        (bool leftNames, bool rightNames) = ReplacementDirections(left, right);
        return Result(FontRelationshipKind.CoverageSuperset, leftContains && leftNames, rightContains && rightNames, "coverage.superset", "glyphs.common-equal");
    }

    private static bool CoverageVariant(FontFaceEvidence left, FontFaceEvidence right)
    {
        if (!SameIdentity(left, right)
            || !SemanticGroupsEqual(left, right, FunctionalGroups.Where(group => group != FontSemanticGroup.Mapping))
            || !CommonCharactersEqual(left, right))
        {
            return false;
        }

        bool leftContains = right.CharacterFingerprints.Keys.All(left.CharacterFingerprints.ContainsKey);
        bool rightContains = left.CharacterFingerprints.Keys.All(right.CharacterFingerprints.ContainsKey);
        if (leftContains || rightContains)
        {
            return false;
        }

        IReadOnlyDictionary<uint, ReadOnlyMemory<byte>> smallerEvidence = left.CharacterFingerprints.Count <= right.CharacterFingerprints.Count
            ? left.CharacterFingerprints
            : right.CharacterFingerprints;
        IReadOnlyDictionary<uint, ReadOnlyMemory<byte>> largerEvidence = ReferenceEquals(smallerEvidence, left.CharacterFingerprints)
            ? right.CharacterFingerprints
            : left.CharacterFingerprints;
        int smaller = smallerEvidence.Count;
        int intersection = smallerEvidence.Keys.Count(largerEvidence.ContainsKey);
        return smaller > 0 && intersection * 100L >= smaller * 95L;
    }

    private static bool SmallGlyphDifference(
        FontFaceEvidence left,
        FontFaceEvidence right,
        uint[] differingGlyphs)
    {
        int comparable = left.CharacterFingerprints.Count;
        return SameIdentity(left, right)
            && CoverageEqual(left, right)
            && SelectorsEqual(left, right)
            && SemanticGroupsEqual(left, right, FunctionalGroups.Where(group => group != FontSemanticGroup.OutlinesAndMetrics))
            && differingGlyphs.Length is > 0 and <= 16
            && comparable > 0
            && differingGlyphs.Length * 100L <= comparable;
    }

    private static bool OnlySemanticGroupDiffers(FontFaceEvidence left, FontFaceEvidence right, FontSemanticGroup differingGroup)
    {
        return SameIdentity(left, right)
            && CoverageEqual(left, right)
            && SelectorsEqual(left, right)
            && CharacterEvidenceEqual(left, right)
            && !SemanticGroupEqual(left, right, differingGroup)
            && SemanticGroupsEqual(left, right, Enum.GetValues<FontSemanticGroup>().Where(group => group != differingGroup));
    }

    private static bool SameIdentity(FontFaceEvidence left, FontFaceEvidence right)
    {
        FontIdentityEvidence first = left.Identity;
        FontIdentityEvidence second = right.Identity;
        if (!StructuralFieldsEqual(left, right) || ConflictingOptional(first.VendorId, second.VendorId))
        {
            return false;
        }

        if (first.FamilyName is not null && second.FamilyName is not null)
        {
            return SameValue(first.FamilyName, second.FamilyName)
                && SameValue(first.SubfamilyName, second.SubfamilyName);
        }

        if (first.FullName is not null && second.FullName is not null)
        {
            return SameValue(first.FullName, second.FullName);
        }

        return first.PostScriptName is not null
            && second.PostScriptName is not null
            && SameValue(first.PostScriptName, second.PostScriptName);
    }

    private static bool StructuralFieldsEqual(FontFaceEvidence left, FontFaceEvidence right)
    {
        return string.Equals(left.Technology, right.Technology, StringComparison.Ordinal)
            && left.UnitsPerEm == right.UnitsPerEm
            && left.IsFixedPitch == right.IsFixedPitch
            && left.Identity.Weight == right.Identity.Weight
            && left.Identity.Width == right.Identity.Width
            && left.Identity.Slant == right.Identity.Slant;
    }

    private static (bool LeftReplacesRight, bool RightReplacesLeft) ReplacementDirections(
        FontFaceEvidence left,
        FontFaceEvidence right)
    {
        bool leftReplaces = left.Identity.AllNames.IsSupersetOf(right.Identity.AllNames);
        bool rightReplaces = right.Identity.AllNames.IsSupersetOf(left.Identity.AllNames);
        return (leftReplaces, rightReplaces);
    }

    private static uint[] DifferingGlyphs(FontFaceEvidence left, FontFaceEvidence right)
    {
        if (!CoverageEqual(left, right))
        {
            return [];
        }

        return left.CharacterFingerprints
            .Where(pair => !right.CharacterFingerprints.TryGetValue(pair.Key, out ReadOnlyMemory<byte> value)
                || !Equal(pair.Value, value))
            .Select(pair => pair.Key)
            .OrderBy(value => value)
            .ToArray();
    }

    private static bool CommonCharactersEqual(FontFaceEvidence left, FontFaceEvidence right)
    {
        IReadOnlyDictionary<uint, ReadOnlyMemory<byte>> smallerEvidence = left.CharacterFingerprints.Count <= right.CharacterFingerprints.Count
            ? left.CharacterFingerprints
            : right.CharacterFingerprints;
        IReadOnlyDictionary<uint, ReadOnlyMemory<byte>> largerEvidence = ReferenceEquals(smallerEvidence, left.CharacterFingerprints)
            ? right.CharacterFingerprints
            : left.CharacterFingerprints;
        return smallerEvidence.All(pair => !largerEvidence.TryGetValue(pair.Key, out ReadOnlyMemory<byte> value)
            || Equal(pair.Value, value));
    }

    private static bool CharacterEvidenceEqual(FontFaceEvidence left, FontFaceEvidence right)
    {
        return left.CharacterFingerprints.Count == right.CharacterFingerprints.Count
            && left.CharacterFingerprints.All(pair => right.CharacterFingerprints.TryGetValue(pair.Key, out ReadOnlyMemory<byte> value)
                && Equal(pair.Value, value));
    }

    private static bool DeepCharacterEvidenceComplete(FontFaceEvidence face)
    {
        if (!face.IsDeepEvidenceComplete)
        {
            return false;
        }

        ulong scalarCount = 0;
        uint? previousEnd = null;
        foreach (UnicodeRange range in face.UnicodeCoverage)
        {
            if (range.Start > range.End
                || range.End > 0x10ffff
                || (previousEnd.HasValue && range.Start <= previousEnd.Value))
            {
                return false;
            }

            scalarCount += (ulong)range.End - range.Start + 1;
            if (scalarCount > int.MaxValue)
            {
                return false;
            }

            for (uint codePoint = range.Start; codePoint <= range.End; codePoint++)
            {
                if (!face.CharacterFingerprints.ContainsKey(codePoint))
                {
                    return false;
                }
            }

            previousEnd = range.End;
        }

        return (ulong)face.CharacterFingerprints.Count == scalarCount;
    }

    private static bool CoverageEqual(FontFaceEvidence left, FontFaceEvidence right)
        => left.CharacterFingerprints.Count == right.CharacterFingerprints.Count
            && left.CharacterFingerprints.Keys.All(right.CharacterFingerprints.ContainsKey);

    private static bool SelectorsEqual(FontFaceEvidence left, FontFaceEvidence right)
        => left.VariationSelectors.SetEquals(right.VariationSelectors)
            && left.VariationFingerprints.Count == right.VariationFingerprints.Count
            && VariationsContain(left, right);

    private static bool VariationsContain(FontFaceEvidence replacement, FontFaceEvidence removed)
        => removed.VariationFingerprints.All(pair => replacement.VariationFingerprints.TryGetValue(pair.Key, out ReadOnlyMemory<byte> value)
            && Equal(pair.Value, value));

    private static bool SemanticGroupsEqual(
        FontFaceEvidence left,
        FontFaceEvidence right,
        IEnumerable<FontSemanticGroup> groups)
        => groups.All(group => SemanticGroupEqual(left, right, group));

    private static bool SemanticGroupEqual(FontFaceEvidence left, FontFaceEvidence right, FontSemanticGroup group)
    {
        bool hasLeft = left.SemanticFingerprints.TryGetValue(group, out ReadOnlyMemory<byte> leftValue);
        bool hasRight = right.SemanticFingerprints.TryGetValue(group, out ReadOnlyMemory<byte> rightValue);
        return hasLeft == hasRight && (!hasLeft || Equal(leftValue, rightValue));
    }

    private static bool ConflictingOptional(string? left, string? right)
        => left is not null && right is not null && !string.Equals(left, right, StringComparison.Ordinal);

    private static bool HasConflictingNameReference(FontIdentityEvidence left, FontIdentityEvidence right)
    {
        return left.AllNames.Intersect(right.AllNames, StringComparer.Ordinal).Any(name =>
            name.Length > 0
            && !((SameValue(name, left.FamilyName) || left.FamilyNames.Contains(name))
                && (SameValue(name, right.FamilyName) || right.FamilyNames.Contains(name)))
            && !((SameValue(name, left.SubfamilyName) || left.SubfamilyNames.Contains(name))
                && (SameValue(name, right.SubfamilyName) || right.SubfamilyNames.Contains(name))));
    }

    private static bool SameValue(string? left, string? right)
        => left is not null && right is not null && string.Equals(left, right, StringComparison.Ordinal);

    private static bool Equal(ReadOnlyMemory<byte> left, ReadOnlyMemory<byte> right)
        => left.Span.SequenceEqual(right.Span);

    private static FontRelationshipResult Result(
        FontRelationshipKind kind,
        bool leftCanReplaceRight,
        bool rightCanReplaceLeft,
        params string[] evidenceKeys)
        => new(kind, leftCanReplaceRight, rightCanReplaceLeft, evidenceKeys, []);
}
