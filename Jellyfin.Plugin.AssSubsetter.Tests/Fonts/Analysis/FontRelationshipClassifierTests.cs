using System.Text;
using Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Analysis;

public sealed class FontRelationshipClassifierTests
{
    private readonly FontRelationshipClassifier _classifier = new();

    public static TheoryData<FontFaceEvidence, FontFaceEvidence, FontRelationshipKind> RelationshipCases()
    {
        FontFaceEvidence basis = Face();
        return new TheoryData<FontFaceEvidence, FontFaceEvidence, FontRelationshipKind>
        {
            { basis, basis with { PhysicalFileId = "right.ttf" }, FontRelationshipKind.IdenticalFileHash },
            { basis, basis with { FileHash = Bytes("other-file"), PhysicalFileId = "right.ttf" }, FontRelationshipKind.StrictFaceDuplicate },
            { basis, basis with { FileHash = Bytes("other-file"), StrictFingerprint = Bytes("other-strict"), PhysicalFileId = "right.ttf" }, FontRelationshipKind.RenderingAndLayoutEquivalent },
            { basis, MetadataVariant(basis), FontRelationshipKind.MetadataOnlyDifference },
            { Face(coverage: Range(0, 100)), DistinctContainer(Face(coverage: Range(0, 99), marker: "right")), FontRelationshipKind.CoverageSuperset },
            { Face(coverage: VariantLeft()), DistinctContainer(Face(coverage: VariantRight(95), marker: "right")), FontRelationshipKind.CoverageVariant },
            { Face(coverage: Range(0, 1599)), GlyphVariant(Face(coverage: Range(0, 1599), marker: "right"), 16), FontRelationshipKind.SmallGlyphDifference },
            { basis, SemanticVariant(basis, FontSemanticGroup.Layout, "layout-b"), FontRelationshipKind.LayoutOnlyDifference },
            { basis, SemanticVariant(basis, FontSemanticGroup.Hinting, "hint-b"), FontRelationshipKind.HintingOnlyDifference },
            { VersionedConflict(basis, 1m), VersionedConflict(basis, 2m, "right"), FontRelationshipKind.VersionConflict },
            { SemanticVariant(basis, FontSemanticGroup.OutlinesAndMetrics, "outline-b"), SemanticVariant(basis, FontSemanticGroup.OutlinesAndMetrics, "outline-c", "right"), FontRelationshipKind.SameIdentityContentConflict },
            { basis, ConflictingIdentity(basis, samePostScript: true), FontRelationshipKind.PostScriptNameConflict },
            { basis, ConflictingIdentity(basis, samePostScript: false, crossName: true), FontRelationshipKind.CrossNameConflict },
            { basis, ConflictingIdentity(basis, samePostScript: false), FontRelationshipKind.Independent }
        };
    }

    [Theory]
    [MemberData(nameof(RelationshipCases))]
    public void Classify_ShouldReturnFirstMatchingRelationship(
        FontFaceEvidence left,
        FontFaceEvidence right,
        FontRelationshipKind expected)
    {
        FontRelationshipResult result = _classifier.Classify(left, right);

        Assert.Equal(expected, result.Kind);
        Assert.NotEmpty(result.EvidenceKeys);
    }

    [Fact]
    public void Classify_ShouldNotMatchDifferentFacesInsideSameIdenticalContainer()
    {
        FontFaceEvidence left = Face();
        FontFaceEvidence right = left with { PhysicalFileId = "right.ttc", FaceIndex = 1 };

        FontRelationshipResult result = _classifier.Classify(left, right);

        Assert.NotEqual(FontRelationshipKind.IdenticalFileHash, result.Kind);
    }

    [Fact]
    public void Classify_ShouldReturnUnclassifiableBeforeHashEvidence()
    {
        FontFaceEvidence left = Face() with { IsClassifiable = false, ClassificationError = "Malformed cmap." };

        FontRelationshipResult result = _classifier.Classify(left, Face(marker: "right"));

        Assert.Equal(FontRelationshipKind.Unclassifiable, result.Kind);
        Assert.False(result.LeftCanReplaceRight);
        Assert.False(result.RightCanReplaceLeft);
    }

    [Fact]
    public void Classify_ShouldReturnUnclassifiableWhenDeepGlyphEvidenceIsIncomplete()
    {
        FontFaceEvidence right = DistinctContainer(Face(marker: "right")) with
        {
            CharacterFingerprints = new Dictionary<uint, ReadOnlyMemory<byte>>()
        };

        FontRelationshipResult result = _classifier.Classify(Face(), right);

        Assert.Equal(FontRelationshipKind.Unclassifiable, result.Kind);
        Assert.Contains("glyphs.incomplete", result.EvidenceKeys);
    }

    [Fact]
    public void Classify_ShouldApplyEarlierHashAndStrictMatchesBeforeSemanticMatches()
    {
        FontFaceEvidence left = Face();

        Assert.Equal(
            FontRelationshipKind.IdenticalFileHash,
            _classifier.Classify(left, left with { PhysicalFileId = "right.ttf" }).Kind);
        Assert.Equal(
            FontRelationshipKind.StrictFaceDuplicate,
            _classifier.Classify(left, left with { PhysicalFileId = "right.ttf", FileHash = Bytes("file-b") }).Kind);
    }

    [Fact]
    public void Classify_ShouldNotTreatFunctionalStyleFieldsAsMetadata()
    {
        FontFaceEvidence left = Face();
        FontFaceEvidence right = MetadataVariant(left) with
        {
            Identity = MetadataVariant(left).Identity with { Weight = 700 }
        };

        FontRelationshipResult result = _classifier.Classify(left, right);

        Assert.NotEqual(FontRelationshipKind.MetadataOnlyDifference, result.Kind);
    }

    [Theory]
    [InlineData(95, FontRelationshipKind.CoverageVariant)]
    [InlineData(94, FontRelationshipKind.SameIdentityContentConflict)]
    public void Classify_ShouldEnforceCoverageVariantIntersectionThreshold(int intersection, FontRelationshipKind expected)
    {
        FontRelationshipResult result = _classifier.Classify(
            Face(coverage: VariantLeft()),
            DistinctContainer(Face(coverage: VariantRight(intersection), marker: "right")));

        Assert.Equal(expected, result.Kind);
    }

    [Theory]
    [InlineData(16, FontRelationshipKind.SmallGlyphDifference)]
    [InlineData(17, FontRelationshipKind.SameIdentityContentConflict)]
    public void Classify_ShouldEnforceBothSmallGlyphDifferenceLimits(int differences, FontRelationshipKind expected)
    {
        FontFaceEvidence left = Face(coverage: Range(0, 1599));
        FontFaceEvidence right = GlyphVariant(Face(coverage: Range(0, 1599), marker: "right"), differences);

        FontRelationshipResult result = _classifier.Classify(left, right);

        Assert.Equal(expected, result.Kind);
        if (expected == FontRelationshipKind.SmallGlyphDifference)
        {
            Assert.Equal(differences, result.DifferingCodePoints.Count);
        }
    }

    [Fact]
    public void Classify_ShouldNotCallLayoutAndHintingChangesEitherOnlyDifference()
    {
        FontFaceEvidence right = SemanticVariant(
            SemanticVariant(Face(marker: "right"), FontSemanticGroup.Layout, "layout-b"),
            FontSemanticGroup.Hinting,
            "hint-b");

        FontRelationshipResult result = _classifier.Classify(Face(), right);

        Assert.Equal(FontRelationshipKind.SameIdentityContentConflict, result.Kind);
    }

    [Fact]
    public void Classify_ShouldUseVersionConflictOnlyWhenBothVersionsAreReliable()
    {
        FontFaceEvidence left = VersionedConflict(Face(), 1m);
        FontFaceEvidence reliable = VersionedConflict(Face(marker: "right"), 2m);
        FontFaceEvidence unreliable = reliable with
        {
            Identity = reliable.Identity with { Version = new FontVersionEvidence(null, false, ["name.5"]) }
        };

        Assert.Equal(FontRelationshipKind.VersionConflict, _classifier.Classify(left, reliable).Kind);
        Assert.Equal(FontRelationshipKind.SameIdentityContentConflict, _classifier.Classify(left, unreliable).Kind);
    }

    [Fact]
    public void Classify_ShouldPreferSameIdentityConflictOverPostScriptCollision()
    {
        FontFaceEvidence right = SemanticVariant(
            SemanticVariant(Face(marker: "right"), FontSemanticGroup.Layout, "layout-b"),
            FontSemanticGroup.Hinting,
            "hint-b");

        FontRelationshipResult result = _classifier.Classify(Face(), right);

        Assert.Equal(FontRelationshipKind.SameIdentityContentConflict, result.Kind);
    }

    [Fact]
    public void Classify_ShouldKeepDistinctStylesInSameFamilyIndependent()
    {
        FontFaceEvidence bold = Face() with
        {
            Identity = Identity("APARAJITA-BOLD", "APARAJITA", "BOLD") with { Weight = 700 }
        };
        FontFaceEvidence boldItalic = SemanticVariant(Face(marker: "italic") with
        {
            Identity = Identity("APARAJITA-BOLDITALIC", "APARAJITA", "BOLD ITALIC") with
            {
                Weight = 700,
                Slant = 1
            }
        }, FontSemanticGroup.OutlinesAndMetrics, "italic-outline", "italic");

        FontRelationshipResult result = _classifier.Classify(bold, boldItalic);

        Assert.Equal(FontRelationshipKind.Independent, result.Kind);
        Assert.False(result.LeftCanReplaceRight);
        Assert.False(result.RightCanReplaceLeft);
    }

    [Fact]
    public void Classify_ShouldNotTreatSharedSubfamilyAsCrossNameConflict()
    {
        FontFaceEvidence first = Face() with
        {
            Identity = Identity("FIRST-BOLD", "FIRST", "BOLD") with
            {
                AllNames = new HashSet<string> { "FIRST", "BOLD", "FIRST BOLD", "FIRST-BOLD" }
            }
        };
        FontFaceEvidence second = SemanticVariant(Face(marker: "second") with
        {
            Identity = Identity("SECOND-BOLD", "SECOND", "BOLD") with
            {
                AllNames = new HashSet<string> { "SECOND", "BOLD", "SECOND BOLD", "SECOND-BOLD" }
            }
        }, FontSemanticGroup.OutlinesAndMetrics, "second-outline", "second");

        Assert.Equal(FontRelationshipKind.Independent, _classifier.Classify(first, second).Kind);
    }

    [Fact]
    public void Classify_ShouldKeepFaceSpecificAliasCollisionAsCrossNameConflict()
    {
        FontFaceEvidence bold = Face() with
        {
            Identity = Identity("APARAJITA-BOLD", "APARAJITA", "BOLD")
        };
        FontFaceEvidence boldItalic = SemanticVariant(Face(marker: "italic") with
        {
            Identity = Identity("APARAJITA-BOLDITALIC", "APARAJITA", "BOLD ITALIC") with
            {
                AllNames = new HashSet<string>
                {
                    "APARAJITA", "APARAJITA BOLD", "APARAJITA BOLD ITALIC", "APARAJITA-BOLDITALIC"
                }
            }
        }, FontSemanticGroup.OutlinesAndMetrics, "italic-outline", "italic");

        Assert.Equal(FontRelationshipKind.CrossNameConflict, _classifier.Classify(bold, boldItalic).Kind);
    }

    [Fact]
    public void Classify_ShouldKeepStylesSharingLegacyFamilyIndependent()
    {
        FontFaceEvidence italic = Face() with
        {
            Identity = ResolvedIdentity(900, 1,
                (1, "Segoe UI Black"), (2, "Italic"), (4, "Segoe UI Black Italic"),
                (6, "SegoeUIBlack-Italic"), (16, "Segoe UI"), (17, "Black Italic"))
        };
        FontFaceEvidence upright = SemanticVariant(Face(marker: "upright") with
        {
            Identity = ResolvedIdentity(900, 0,
                (1, "Segoe UI Black"), (2, "Regular"), (4, "Segoe UI Black"),
                (6, "SegoeUIBlack"), (16, "Segoe UI"), (17, "Black"))
        }, FontSemanticGroup.OutlinesAndMetrics, "upright-outline", "upright");

        Assert.Equal(FontRelationshipKind.Independent, _classifier.Classify(italic, upright).Kind);
    }

    [Fact]
    public void Classify_ShouldIgnoreSharedLegacyStyleWhenPreferredStylesDiffer()
    {
        FontFaceEvidence semilight = Face() with
        {
            Identity = ResolvedIdentity(350, 0,
                (1, "Leelawadee UI Semilight"), (2, "Regular"), (4, "Leelawadee UI Semilight"),
                (6, "LeelawadeeUI-Semilight"), (16, "Leelawadee UI"), (17, "Semilight"))
        };
        FontFaceEvidence regular = SemanticVariant(Face(marker: "regular") with
        {
            Identity = ResolvedIdentity(400, 0,
                (1, "Leelawadee UI"), (2, "Regular"), (4, "Leelawadee UI"),
                (6, "LeelawadeeUI"))
        }, FontSemanticGroup.OutlinesAndMetrics, "regular-outline", "regular");

        Assert.Equal(FontRelationshipKind.Independent, _classifier.Classify(semilight, regular).Kind);
    }

    [Fact]
    public void Classify_ShouldRefuseEquivalenceWhenUnknownFunctionalEvidenceDiffers()
    {
        FontFaceEvidence left = SemanticVariant(Face(), FontSemanticGroup.OtherFunctional, "unknown-a");
        FontFaceEvidence right = SemanticVariant(Face(marker: "right"), FontSemanticGroup.OtherFunctional, "unknown-b");

        FontRelationshipResult result = _classifier.Classify(left, right);

        Assert.Equal(FontRelationshipKind.SameIdentityContentConflict, result.Kind);
    }

    [Fact]
    public void Classify_ShouldSetDirectionalReplacementFlags()
    {
        FontRelationshipResult identical = _classifier.Classify(Face(), Face(marker: "right"));
        FontRelationshipResult superset = _classifier.Classify(
            Face(coverage: Range(0, 100)),
            DistinctContainer(Face(coverage: Range(0, 99), marker: "right")));

        Assert.True(identical.LeftCanReplaceRight);
        Assert.True(identical.RightCanReplaceLeft);
        Assert.True(superset.LeftCanReplaceRight);
        Assert.False(superset.RightCanReplaceLeft);
    }

    [Fact]
    public void Classify_CoverageSupersetShouldNotReplaceMissingLookupAlias()
    {
        FontFaceEvidence larger = Face(coverage: Range(0, 100));
        FontFaceEvidence smaller = DistinctContainer(Face(coverage: Range(0, 99), marker: "right"));
        smaller = smaller with
        {
            Identity = smaller.Identity with
            {
                AllNames = new HashSet<string>(smaller.Identity.AllNames) { "EXAMPLE ALIAS" }
            }
        };

        FontRelationshipResult result = _classifier.Classify(larger, smaller);

        Assert.Equal(FontRelationshipKind.CoverageSuperset, result.Kind);
        Assert.False(result.LeftCanReplaceRight);
        Assert.False(result.RightCanReplaceLeft);
    }

    [Fact]
    public void Classify_AdditionalVariationSelectorShouldBeCoverageSuperset()
    {
        FontFaceEvidence smaller = DistinctContainer(Face(marker: "right")) with
        {
            VariationSelectors = new HashSet<uint>()
        };
        FontFaceEvidence larger = SemanticVariant(Face(), FontSemanticGroup.Mapping, "extra-variation") with
        {
            VariationFingerprints = new Dictionary<ulong, ReadOnlyMemory<byte>>
            {
                [((ulong)0xfe0f << 21) | 65] = Bytes("variation-A")
            }
        };

        FontRelationshipResult result = _classifier.Classify(larger, smaller);

        Assert.Equal(FontRelationshipKind.CoverageSuperset, result.Kind);
        Assert.True(result.LeftCanReplaceRight);
        Assert.False(result.RightCanReplaceLeft);
    }

    [Fact]
    public void Classify_AdditionalVariationMappingWithSameSelectorShouldBeCoverageSuperset()
    {
        ulong existingMapping = ((ulong)0xfe0f << 21) | 65;
        ulong addedMapping = ((ulong)0xfe0f << 21) | 66;
        FontFaceEvidence smaller = DistinctContainer(Face(marker: "right")) with
        {
            VariationFingerprints = new Dictionary<ulong, ReadOnlyMemory<byte>>
            {
                [existingMapping] = Bytes("variation-A")
            }
        };
        FontFaceEvidence larger = SemanticVariant(Face(), FontSemanticGroup.Mapping, "extra-variation") with
        {
            VariationFingerprints = new Dictionary<ulong, ReadOnlyMemory<byte>>
            {
                [existingMapping] = Bytes("variation-A"),
                [addedMapping] = Bytes("variation-B")
            }
        };

        FontRelationshipResult result = _classifier.Classify(larger, smaller);

        Assert.Equal(FontRelationshipKind.CoverageSuperset, result.Kind);
        Assert.True(result.LeftCanReplaceRight);
        Assert.False(result.RightCanReplaceLeft);
    }

    private static FontFaceEvidence Face(IReadOnlyList<UnicodeRange>? coverage = null, string marker = "left")
    {
        IReadOnlyList<UnicodeRange> actualCoverage = coverage ?? Range(0, 99);
        return new FontFaceEvidence
        {
            IsClassifiable = true,
            IsDeepEvidenceComplete = true,
            PhysicalFileId = marker + ".ttf",
            FaceIndex = 0,
            FileHash = Bytes("same-file"),
            StrictFingerprint = Bytes("same-strict"),
            SemanticFingerprints = Semantics(),
            Identity = Identity(),
            Technology = "TrueType",
            UnitsPerEm = 1000,
            IsFixedPitch = false,
            UnicodeCoverage = actualCoverage,
            VariationSelectors = new HashSet<uint> { 0xfe0f },
            CharacterFingerprints = Glyphs(actualCoverage)
        };
    }

    private static FontIdentityEvidence Identity(
        string postScript = "EXAMPLE-REGULAR",
        string family = "EXAMPLE",
        string style = "REGULAR",
        IReadOnlySet<string>? allNames = null)
    {
        return new FontIdentityEvidence(
            postScript,
            family,
            style,
            16,
            17,
            family + " " + style,
            "VENDOR:" + postScript,
            postScript,
            allNames ?? new HashSet<string> { postScript, family, family + " " + style },
            "TEST",
            400,
            5,
            0,
            new FontVersionEvidence(null, false, []));
    }

    private static FontIdentityEvidence ResolvedIdentity(int weight, int slant, params (ushort Id, string Value)[] names)
    {
        return FontIdentityResolver.Resolve(new InspectedFontFace
        {
            FamilyName = "Unused",
            StyleName = "Unused",
            Technology = "TrueType",
            Weight = weight,
            Width = 5,
            Slant = slant,
            NameRecords = names.Select(name => new OpenTypeNameRecord(3, 1, 0x0409, name.Id, name.Value)).ToArray()
        });
    }

    private static FontFaceEvidence MetadataVariant(FontFaceEvidence face)
    {
        FontIdentityEvidence identity = face.Identity with
        {
            AllNames = new HashSet<string>(face.Identity.AllNames) { "EXAMPLE ALIAS" }
        };
        return SemanticVariant(face with { Identity = identity, PhysicalFileId = "metadata.ttf" }, FontSemanticGroup.Metadata, "metadata-b");
    }

    private static FontFaceEvidence DistinctContainer(FontFaceEvidence face)
    {
        return face with
        {
            FileHash = Bytes("different-file"),
            StrictFingerprint = Bytes("different-strict")
        };
    }

    private static FontFaceEvidence SemanticVariant(
        FontFaceEvidence face,
        FontSemanticGroup group,
        string value,
        string? marker = null)
    {
        var semantics = new Dictionary<FontSemanticGroup, ReadOnlyMemory<byte>>(face.SemanticFingerprints)
        {
            [group] = Bytes(value)
        };
        return face with
        {
            PhysicalFileId = (marker ?? "semantic") + ".ttf",
            FileHash = Bytes("different-file-" + value),
            StrictFingerprint = Bytes("different-strict-" + value),
            SemanticFingerprints = semantics
        };
    }

    private static FontFaceEvidence GlyphVariant(FontFaceEvidence face, int differences)
    {
        var glyphs = new Dictionary<uint, ReadOnlyMemory<byte>>(face.CharacterFingerprints);
        for (uint codePoint = 0; codePoint < differences; codePoint++)
        {
            glyphs[codePoint] = Bytes("different-" + codePoint);
        }

        return SemanticVariant(face with { CharacterFingerprints = glyphs }, FontSemanticGroup.OutlinesAndMetrics, "outline-b");
    }

    private static FontFaceEvidence VersionedConflict(FontFaceEvidence face, decimal version, string marker = "version")
    {
        FontFaceEvidence changed = SemanticVariant(face, FontSemanticGroup.Layout, marker + "-layout-" + version, marker);
        changed = SemanticVariant(changed, FontSemanticGroup.Hinting, marker + "-hint-" + version, marker);
        return changed with
        {
            Identity = changed.Identity with
            {
                Version = new FontVersionEvidence(version, true, ["head.fontRevision", "name.5"])
            }
        };
    }

    private static FontFaceEvidence ConflictingIdentity(FontFaceEvidence face, bool samePostScript, bool crossName = false)
    {
        string postScript = samePostScript ? "EXAMPLE-REGULAR" : "OTHER-REGULAR";
        IReadOnlySet<string> names = crossName
            ? new HashSet<string> { postScript, "EXAMPLE" }
            : new HashSet<string> { postScript, "OTHER" };
        return SemanticVariant(face with
        {
            Identity = Identity(postScript, "OTHER", "BOLD", names)
        }, FontSemanticGroup.OutlinesAndMetrics, "other-outline", "conflict");
    }

    private static IReadOnlyDictionary<FontSemanticGroup, ReadOnlyMemory<byte>> Semantics()
    {
        return Enum.GetValues<FontSemanticGroup>()
            .Where(group => group != FontSemanticGroup.OtherFunctional)
            .ToDictionary(group => group, group => Bytes(group.ToString()));
    }

    private static IReadOnlyDictionary<uint, ReadOnlyMemory<byte>> Glyphs(IEnumerable<UnicodeRange> coverage)
    {
        var result = new Dictionary<uint, ReadOnlyMemory<byte>>();
        foreach (UnicodeRange range in coverage)
        {
            for (uint value = range.Start; value <= range.End; value++)
            {
                result[value] = Bytes("glyph-" + value);
            }
        }

        return result;
    }

    private static IReadOnlyList<UnicodeRange> Range(uint start, uint end) => [new(start, end)];

    private static IReadOnlyList<UnicodeRange> VariantLeft() => [new(0, 99), new(1000, 1000)];

    private static IReadOnlyList<UnicodeRange> VariantRight(int intersection)
        => [new(0, (uint)intersection - 1), new(200, (uint)(299 - intersection))];

    private static ReadOnlyMemory<byte> Bytes(string value) => Encoding.UTF8.GetBytes(value);
}
