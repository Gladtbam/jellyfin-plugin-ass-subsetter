using Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Analysis;

public sealed class FontRetentionSelectorTests
{
    private readonly FontRetentionSelector _selector = new();

    [Theory]
    [InlineData(false, false, 300)]
    [InlineData(true, false, 200)]
    [InlineData(false, true, 100)]
    public void Select_ShouldOptimizeDirectRetainedFileCombinationsAfterQuality(bool betterCollection, bool missingProof, long reclaimed)
    {
        DirectoryPolicySnapshot snapshot = Snapshot(
            File(1, "AB.ttc", 300, Face(1), Face(2)),
            File(2, "A.ttf", 100, Face(3, CompleteSignals: !betterCollection)),
            File(3, "B.ttf", 100, Face(4, CompleteSignals: !betterCollection)));
        FontRelationRecord[] relations = missingProof ? [BothDirections(1, 3)] : [BothDirections(1, 3), BothDirections(2, 4)];

        RetentionPlan plan = _selector.Select(relations, snapshot);

        Assert.Equal(reclaimed, plan.ReclaimableBytes);
        if (!betterCollection && !missingProof)
        {
            Assert.Equal([2L, 3L], plan.RetainedFiles.Select(file => file.FileId).Order().ToArray());
            Assert.Equal(1L, Assert.Single(plan.QuarantineFiles).FileId);
        }
        else
        {
            Assert.Contains(plan.RetainedFiles, file => file.FileId == 1);
        }
    }

    [Fact]
    public void Select_ShouldNotInferAReplacesCThroughB()
    {
        DirectoryPolicySnapshot snapshot = Snapshot(
            File(1, "A.ttf", 100, Face(1)),
            File(2, "B.ttf", 100, Face(2)),
            File(3, "C.ttf", 100, Face(3)));
        FontRelationRecord[] relations =
        [
            Replaces(1, 2),
            Replaces(2, 3)
        ];

        RetentionPlan plan = _selector.Select(relations, snapshot);

        Assert.Contains(plan.QuarantineFiles, file => file.FileId == 2);
        Assert.Contains(plan.RetainedFiles, file => file.FileId == 3);
    }

    [Fact]
    public void Select_ShouldKeepCollectionContainingUniqueFace()
    {
        DirectoryPolicySnapshot snapshot = Snapshot(
            File(1, "mixed.ttc", 500, Face(1), Face(2)),
            File(2, "duplicate.ttf", 100, Face(3)));

        RetentionPlan plan = _selector.Select([Replaces(3, 1)], snapshot);

        Assert.Contains(plan.RetainedFiles, file => file.Name == "mixed.ttc");
        Assert.DoesNotContain(plan.QuarantineFiles, file => file.Name == "mixed.ttc");
    }

    [Fact]
    public void Select_ShouldNeverQuarantineProtectedFileOrDiscardHigherQualityCustomCopy()
    {
        DirectoryPolicySnapshot snapshot = Snapshot(
            File(1, "system.ttf", 100, Face(1, CompleteSignals: false), FontDirectoryPolicy.Protected),
            File(2, "custom.ttf", 100, Face(2, CompleteSignals: true), FontDirectoryPolicy.Custom));

        RetentionPlan plan = _selector.Select([BothDirections(1, 2)], snapshot);

        Assert.Contains(plan.RetainedFiles, file => file.FileId == 1);
        RetentionFileDecision custom = Assert.Single(plan.RetainedFiles, file => file.FileId == 2);
        Assert.True(custom.IsRuntimePreferred);
        Assert.Empty(plan.QuarantineFiles);
    }

    [Fact]
    public void Select_ShouldAllowProtectedHigherQualityFileToRetainCustomReplacement()
    {
        DirectoryPolicySnapshot snapshot = Snapshot(
            File(1, "system.ttf", 100, Face(1, CompleteSignals: true), FontDirectoryPolicy.Protected),
            File(2, "custom.ttf", 100, Face(2, CompleteSignals: false), FontDirectoryPolicy.Custom));

        RetentionPlan plan = _selector.Select([BothDirections(1, 2)], snapshot);

        Assert.Contains(plan.QuarantineFiles, file => file.FileId == 2);
        Assert.Contains(plan.RetainedFiles, file => file.FileId == 1);
    }

    [Fact]
    public void Select_ShouldRequireCompatibleTechnologyAndAutomaticActionBoundary()
    {
        DirectoryPolicySnapshot technologySnapshot = Snapshot(
            File(1, "truetype.ttf", 100, Face(1, Technology: "TrueType")),
            File(2, "cff.otf", 100, Face(2, Technology: "OpenType CFF")));
        DirectoryPolicySnapshot reportOnlySnapshot = Snapshot(
            File(1, "variant-a.ttf", 100, Face(1)),
            File(2, "variant-b.ttf", 100, Face(2)));

        RetentionPlan technologyPlan = _selector.Select([BothDirections(1, 2)], technologySnapshot);
        RetentionPlan reportOnlyPlan = _selector.Select(
            [BothDirections(1, 2) with { RelationshipKind = FontRelationshipKind.CoverageVariant }],
            reportOnlySnapshot);

        Assert.Empty(technologyPlan.QuarantineFiles);
        Assert.Empty(reportOnlyPlan.QuarantineFiles);
    }

    [Fact]
    public void Select_ShouldRequirePersistedMetadataAliasDirection()
    {
        DirectoryPolicySnapshot snapshot = Snapshot(
            File(1, "alias-a.ttf", 100, Face(1)),
            File(2, "alias-b.ttf", 100, Face(2)));
        FontRelationRecord metadataWithoutDirection = BothDirections(1, 2) with
        {
            RelationshipKind = FontRelationshipKind.MetadataOnlyDifference,
            ReplacementDirection = FontReplacementDirection.None
        };

        RetentionPlan plan = _selector.Select([metadataWithoutDirection], snapshot);

        Assert.Empty(plan.QuarantineFiles);
    }

    [Fact]
    public void Select_ShouldKeepLinkedUnhealthyInUseAndUnclassifiableFiles()
    {
        FontRetentionFile linked = File(2, "linked.ttf", 100, Face(2)) with
        {
            LinkKind = FontLinkKind.SymbolicOrReparsePoint
        };
        FontRetentionFile unhealthy = File(3, "unhealthy.ttf", 100, Face(3)) with
        {
            Health = FontFileHealth.Unclassifiable
        };
        FontRetentionFile inUse = File(4, "in-use.ttf", 100, Face(4)) with
        {
            IsInUseWithoutLease = true
        };
        FontRetentionFile unclassifiable = File(5, "unclassifiable.ttf", 100, Face(5) with
        {
            IsClassifiable = false
        });
        DirectoryPolicySnapshot snapshot = Snapshot(
            File(1, "retained.ttf", 100, Face(1)),
            linked,
            unhealthy,
            inUse,
            unclassifiable);

        RetentionPlan plan = _selector.Select(
            [BothDirections(1, 2), BothDirections(1, 3), BothDirections(1, 4), BothDirections(1, 5)],
            snapshot);

        Assert.All([2L, 3L, 4L, 5L], fileId =>
        {
            Assert.Contains(plan.RetainedFiles, file => file.FileId == fileId);
            Assert.DoesNotContain(plan.QuarantineFiles, file => file.FileId == fileId);
        });
    }

    [Fact]
    public void Select_ShouldUseFixedScoreMaximaAndPersistSubreasons()
    {
        RetentionPlan plan = _selector.Select([], Snapshot(File(1, "complete.ttf", 100, Face(1, CompleteSignals: true))));

        RetentionScore score = Assert.Single(plan.RetainedFiles).Score;
        Assert.Equal(40, score.Content);
        Assert.Equal(25, score.Version);
        Assert.Equal(20, score.Technical);
        Assert.Equal(10, score.Names);
        Assert.Equal(5, score.Modified);
        Assert.Equal(100, score.Total);
        Assert.NotEmpty(score.Subreasons);
    }

    [Fact]
    public void Select_ShouldApplyPreferredDirectoryOnlyAfterEqualQuality()
    {
        DirectoryPolicySnapshot snapshot = Snapshot(
            File(1, "ordinary.ttf", 100, Face(1)),
            File(2, "preferred.ttf", 100, Face(2), FontDirectoryPolicy.Preferred));

        RetentionPlan plan = _selector.Select([BothDirections(1, 2)], snapshot);

        Assert.Contains(plan.RetainedFiles, file => file.FileId == 2);
        Assert.Contains(plan.QuarantineFiles, file => file.FileId == 1);
    }

    [Fact]
    public void Select_ShouldChooseQualityBeforeReclaimableBytes()
    {
        DirectoryPolicySnapshot snapshot = Snapshot(
            File(1, "quality.ttf", 1_000, Face(1, CompleteSignals: true)),
            File(2, "large-low-quality.ttf", 10_000, Face(2, CompleteSignals: false)));

        RetentionPlan plan = _selector.Select([BothDirections(1, 2)], snapshot);

        Assert.Contains(plan.RetainedFiles, file => file.FileId == 1);
        Assert.Contains(plan.QuarantineFiles, file => file.FileId == 2);
    }

    [Fact]
    public void Select_ShouldChooseReclaimableBytesThenNormalizedPathForEqualQuality()
    {
        RetentionPlan bytesPlan = _selector.Select(
            [BothDirections(1, 2)],
            Snapshot(File(1, "small.ttf", 100, Face(1)), File(2, "large.ttf", 1_000, Face(2))));
        RetentionPlan pathPlan = _selector.Select(
            [BothDirections(1, 2)],
            Snapshot(File(1, "zeta.ttf", 100, Face(1)), File(2, "alpha.ttf", 100, Face(2))));

        Assert.Contains(bytesPlan.RetainedFiles, file => file.FileId == 1);
        Assert.Contains(pathPlan.RetainedFiles, file => file.Name == "alpha.ttf");
    }

    [Fact]
    public void Select_ShouldChooseFileCountAfterEqualReclaimableBytes()
    {
        DirectoryPolicySnapshot snapshot = Snapshot(
            File(1, "count-two.ttf", 1_000, Face(1)),
            File(2, "count-one.ttf", 1_000, Face(2)),
            File(3, "small-a.ttf", 50, Face(3)),
            File(4, "small-b.ttf", 50, Face(4)),
            File(5, "large.ttf", 100, Face(5)));

        RetentionPlan plan = _selector.Select(
            [Replaces(1, 3), Replaces(1, 4), Replaces(2, 5)],
            snapshot);

        Assert.Equal(1, Assert.Single(plan.Groups, group => group.QuarantineFileIds.Count == 2).RetainedFileId);
        Assert.Equal(1, plan.Groups[0].RetainedFileId);
    }

    [Fact]
    public void Select_ShouldNeverQuarantineAFileUsedByAnEarlierCombination()
    {
        DirectoryPolicySnapshot snapshot = Snapshot(
            File(1, "a-collection.ttc", 600, [Face(1), Face(2)], FontDirectoryPolicy.None),
            File(2, "b-collection.ttc", 300, [Face(3), Face(4)], FontDirectoryPolicy.None),
            File(3, "c.ttf", 100, Face(5)),
            File(4, "d.ttf", 50, Face(6)));
        FontRelationRecord[] relations =
        [
            Replaces(3, 1), Replaces(5, 2),
            Replaces(6, 3), Replaces(6, 4)
        ];

        RetentionPlan plan = _selector.Select(relations, snapshot);

        HashSet<long> retained = plan.RetainedFiles.Select(file => file.FileId).ToHashSet();
        HashSet<long> quarantined = plan.QuarantineFiles.Select(file => file.FileId).ToHashSet();
        Assert.Empty(retained.Intersect(quarantined));
        Assert.All(plan.Groups, group => Assert.All(group.RetainedFileIds, fileId => Assert.Contains(fileId, retained)));
        Assert.Contains(2L, retained);
    }

    [Fact]
    public void Select_ShouldFindMinimumRetainedBytesInsteadOfGreedyCoverage()
    {
        DirectoryPolicySnapshot snapshot = Snapshot(
            File(1, "target.ttc", 1_000, [Face(1), Face(2), Face(3), Face(4), Face(5), Face(6)], FontDirectoryPolicy.None),
            File(2, "four.ttf", 400, Face(7)),
            File(3, "first-three.ttf", 310, Face(8)),
            File(4, "last-three.ttf", 310, Face(9)));
        FontRelationRecord[] relations =
        [
            Replaces(7, 1), Replaces(7, 2), Replaces(7, 3), Replaces(7, 4),
            Replaces(8, 1), Replaces(8, 2), Replaces(8, 3),
            Replaces(9, 4), Replaces(9, 5), Replaces(9, 6)
        ];

        RetentionPlan plan = _selector.Select(relations, snapshot);

        DuplicateGroupPlan group = Assert.Single(plan.Groups, item => item.QuarantineFileIds.Contains(1));
        Assert.Equal([3L, 4L], group.RetainedFileIds.Order().ToArray());
    }

    [Fact]
    public void Select_ShouldTreatMandatoryRetainedFilesAsZeroIncrementalCost()
    {
        DirectoryPolicySnapshot snapshot = Snapshot(
            File(1, "target.ttc", 150, [Face(1, CompleteSignals: false), Face(2, CompleteSignals: false)], FontDirectoryPolicy.None),
            File(2, "system-a.ttf", 100, Face(3), FontDirectoryPolicy.Protected),
            File(3, "system-b.ttf", 100, Face(4), FontDirectoryPolicy.Protected));

        RetentionPlan plan = _selector.Select([Replaces(3, 1), Replaces(4, 2)], snapshot);

        Assert.Equal(150, plan.ReclaimableBytes);
        Assert.Contains(plan.QuarantineFiles, file => file.FileId == 1);
        Assert.All([2L, 3L], fileId => Assert.Contains(plan.RetainedFiles, file => file.FileId == fileId));
    }

    [Fact]
    public void Select_ShouldReconsiderLocalCombinationWhenOneFileDirectlyReplacesBothTargetsAndParts()
    {
        DirectoryPolicySnapshot snapshot = Snapshot(
            File(1, "a-target.ttc", 300, [Face(1), Face(2)], FontDirectoryPolicy.None),
            File(2, "b-target.ttc", 300, [Face(3), Face(4)], FontDirectoryPolicy.None),
            File(3, "part-a.ttf", 100, Face(5)),
            File(4, "part-b.ttf", 100, Face(6)),
            File(5, "complete.ttf", 210, Face(7)));
        FontRelationRecord[] relations =
        [
            Replaces(5, 1), Replaces(6, 2),
            Replaces(7, 1), Replaces(7, 2), Replaces(7, 3), Replaces(7, 4),
            Replaces(7, 5), Replaces(7, 6)
        ];

        RetentionPlan plan = _selector.Select(relations, snapshot);

        Assert.Equal([5L], plan.RetainedFiles.Select(file => file.FileId).Order().ToArray());
        Assert.Equal([1L, 2L, 3L, 4L], plan.QuarantineFiles.Select(file => file.FileId).Order().ToArray());
    }

    [Fact]
    public void Select_ShouldHandleTwentyThousandIndependentFilesWithoutCreatingReplacementGroups()
    {
        FontRetentionFile[] files = Enumerable.Range(1, 20_000)
            .Select(index => File(index, "font-" + index.ToString("D5", System.Globalization.CultureInfo.InvariantCulture) + ".ttf", 100, Face(index)))
            .ToArray();

        RetentionPlan plan = _selector.Select([], Snapshot(files));

        Assert.Equal(20_000, plan.RetainedFiles.Count);
        Assert.Empty(plan.QuarantineFiles);
        Assert.Empty(plan.Groups);
    }

    [Fact]
    public void Select_ShouldKeepOversizedCollectionComponentReportOnly()
    {
        FontRetentionFile target = File(1, "target.ttc", 300, [Face(1), Face(2)], FontDirectoryPolicy.None);
        FontRetentionFile[] copies = Enumerable.Range(2, 18)
            .Select(index => File(index, "copy-" + index.ToString("D2", System.Globalization.CultureInfo.InvariantCulture) + ".ttf", 100, Face(index + 1)))
            .ToArray();
        FontRelationRecord[] relations = copies.Select(file => Replaces(file.Faces[0].FaceId, 1))
            .Append(Replaces(copies[0].Faces[0].FaceId, 2))
            .ToArray();

        RetentionPlan plan = _selector.Select(relations, Snapshot([target, .. copies]));

        Assert.Equal(19, plan.RetainedFiles.Count);
        Assert.Empty(plan.QuarantineFiles);
    }

    private static DirectoryPolicySnapshot Snapshot(params FontRetentionFile[] files) => new(files);

    private static FontRetentionFile File(
        long fileId,
        string path,
        long size,
        FontRetentionFace face,
        FontDirectoryPolicy policy = FontDirectoryPolicy.None)
        => File(fileId, path, size, [face], policy);

    private static FontRetentionFile File(
        long fileId,
        string path,
        long size,
        FontRetentionFace first,
        FontRetentionFace second)
        => File(fileId, path, size, [first, second], FontDirectoryPolicy.None);

    private static FontRetentionFile File(
        long fileId,
        string path,
        long size,
        IReadOnlyList<FontRetentionFace> faces,
        FontDirectoryPolicy policy)
    {
        return new FontRetentionFile
        {
            FileId = fileId,
            Path = path,
            NormalizedPath = path.ToUpperInvariant(),
            Size = size,
            ModifiedUtc = DateTimeOffset.UnixEpoch,
            DirectoryPolicy = policy,
            LinkKind = FontLinkKind.None,
            Health = FontFileHealth.Healthy,
            Faces = faces
        };
    }

    private static FontRetentionFace Face(
        long faceId,
        bool CompleteSignals = true,
        string Technology = "TrueType")
    {
        IReadOnlySet<FontQualitySignal> signals = CompleteSignals
            ? Enum.GetValues<FontQualitySignal>().ToHashSet()
            : new HashSet<FontQualitySignal>();
        return new FontRetentionFace(
            faceId,
            Technology,
            true,
            new FontRetentionQualityEvidence(signals, CompleteSignals ? 2m : null));
    }

    private static FontRelationRecord Replaces(long retainedFaceId, long replacedFaceId)
    {
        long left = Math.Min(retainedFaceId, replacedFaceId);
        long right = Math.Max(retainedFaceId, replacedFaceId);
        FontReplacementDirection direction = retainedFaceId == left
            ? FontReplacementDirection.LeftReplacesRight
            : FontReplacementDirection.RightReplacesLeft;
        return Relation(left, right, direction);
    }

    private static FontRelationRecord BothDirections(long leftFaceId, long rightFaceId)
        => Relation(leftFaceId, rightFaceId, FontReplacementDirection.Both);

    private static FontRelationRecord Relation(long leftFaceId, long rightFaceId, FontReplacementDirection direction)
    {
        return new FontRelationRecord
        {
            LeftFaceId = Math.Min(leftFaceId, rightFaceId),
            RightFaceId = Math.Max(leftFaceId, rightFaceId),
            RelationshipKind = FontRelationshipKind.StrictFaceDuplicate,
            ReplacementDirection = direction,
            EvidenceVersion = 1,
            EvidenceJson = "{}"
        };
    }
}
