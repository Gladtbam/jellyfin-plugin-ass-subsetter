using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Persistence;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Analysis;

public sealed class FontDuplicateAnalyzerTests
{
    [Fact]
    public async Task AnalyzeAsync_ShouldNotPublishOrPersistABatch()
    {
        var repository = new InMemoryFontRepository();
        var source = new StubEvidenceSource(Snapshot());
        var analyzer = new FontDuplicateAnalyzer(
            source,
            new FontRelationshipClassifier(),
            new FontRetentionSelector(),
            new FixedTimeProvider(DateTimeOffset.Parse("2026-08-02T12:00:00Z")));

        AnalysisBatch result = await analyzer.AnalyzeAsync(42, null, CancellationToken.None);

        Assert.Equal(0, result.Id);
        Assert.Equal(AnalysisBatchStatus.Complete, result.Status);
        Assert.Equal(42, result.GenerationId);
        Assert.True(result.CandidateCount > 0);
        Assert.Equal(1, result.ComparisonCount);
        FontRelationRecord relation = Assert.Single(result.Relations);
        Assert.Equal(FontRelationshipKind.IdenticalFileHash, relation.RelationshipKind);
        Assert.Equal(FontReplacementDirection.Both, relation.ReplacementDirection);
        DuplicateGroupRecord group = Assert.Single(result.Groups);
        Assert.Equal(200, group.ReclaimableBytes);
        Assert.Contains(group.Members, member => member.Decision == DuplicateGroupMemberDecision.QuarantineCandidate);
        Assert.Equal(200, result.ReclaimableBytes);

        using JsonDocument summary = JsonDocument.Parse(result.SummaryJson);
        Assert.Equal(1, summary.RootElement.GetProperty("comparisonCount").GetInt64());
        Assert.Equal(1, summary.RootElement.GetProperty("classCounts").GetProperty("IdenticalFileHash").GetInt32());
        AnalysisBatch? persisted = await repository.GetAnalysisBatchAsync(result.Id, CancellationToken.None);
        Assert.Null(persisted);
        Assert.Null(await repository.GetLatestCompleteAnalysisAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldPersistSmallGlyphDifferenceTypesAndAffectedCodePoints()
    {
        var characters = Enumerable.Range(0x41, 100).ToDictionary(value => (uint)value, _ => Bytes("glyph"));
        FontFaceEvidence left = Evidence(1, 10, "left.ttf", 100) with
        {
            FileHash = Bytes("left-file"),
            StrictFingerprint = Bytes("left-face"),
            UnicodeCoverage = [new UnicodeRange(0x41, 0xa4)],
            CharacterFingerprints = characters,
            SemanticFingerprints = new Dictionary<FontSemanticGroup, ReadOnlyMemory<byte>>
            {
                [FontSemanticGroup.OutlinesAndMetrics] = Bytes("left-outlines")
            }
        };
        var changedCharacters = new Dictionary<uint, ReadOnlyMemory<byte>>(characters) { [0x42] = Bytes("changed-glyph") };
        FontFaceEvidence right = left with
        {
            FaceId = 2,
            FileId = 20,
            PhysicalFileId = "right.ttf",
            FileHash = Bytes("right-file"),
            StrictFingerprint = Bytes("right-face"),
            CharacterFingerprints = changedCharacters,
            SemanticFingerprints = new Dictionary<FontSemanticGroup, ReadOnlyMemory<byte>>
            {
                [FontSemanticGroup.OutlinesAndMetrics] = Bytes("right-outlines")
            }
        };
        var repository = new InMemoryFontRepository();
        var source = new StubEvidenceSource(new FontAnalysisSnapshot(
            [left, right],
            new DirectoryPolicySnapshot([
                RetentionFile(10, "left.ttf", 100, 1),
                RetentionFile(20, "right.ttf", 100, 2)
            ])));
        var analyzer = new FontDuplicateAnalyzer(
            source, new FontRelationshipClassifier(), new FontRetentionSelector(), TimeProvider.System);

        AnalysisBatch result = await analyzer.AnalyzeAsync(42, null, CancellationToken.None);
        AnalysisBatch persisted = result;

        FontRelationRecord relation = Assert.Single(persisted.Relations);
        Assert.Equal(FontRelationshipKind.SmallGlyphDifference, relation.RelationshipKind);
        Assert.Equal(FontReplacementDirection.None, relation.ReplacementDirection);
        using JsonDocument evidence = JsonDocument.Parse(relation.EvidenceJson);
        Assert.Equal([0x42U], evidence.RootElement.GetProperty("differingCodePoints").EnumerateArray().Select(value => value.GetUInt32()));
        Assert.Equal(["OutlinesAndMetrics"], evidence.RootElement.GetProperty("differenceTypes").EnumerateArray().Select(value => value.GetString()));
    }
    [Fact]
    public async Task AnalyzeAsync_ShouldPersistSpecificIncompleteEvidenceReasons()
    {
        FontFaceEvidence left = Evidence(1, 10, "left.ttf", 100) with
        {
            IsClassifiable = false,
            IsDeepEvidenceComplete = false,
            ClassificationError = "Glyph 98: unsupported component flag"
        };
        FontFaceEvidence right = Evidence(2, 20, "right.ttf", 100) with
        {
            FileHash = Bytes("different-file"),
            StrictFingerprint = Bytes("different-face")
        };
        var source = new StubEvidenceSource(new FontAnalysisSnapshot(
            [left, right],
            new DirectoryPolicySnapshot([
                RetentionFile(10, "left.ttf", 100, 1),
                RetentionFile(20, "right.ttf", 100, 2)
            ])));
        var analyzer = new FontDuplicateAnalyzer(
            source, new FontRelationshipClassifier(),
            new FontRetentionSelector(), TimeProvider.System);

        AnalysisBatch result = await analyzer.AnalyzeAsync(42, null, CancellationToken.None);

        FontRelationRecord relation = Assert.Single(result.Relations);
        Assert.Equal(FontRelationshipKind.Unclassifiable, relation.RelationshipKind);
        using JsonDocument evidence = JsonDocument.Parse(relation.EvidenceJson);
        Assert.Contains("Glyph 98", evidence.RootElement.GetProperty("leftDiagnostic").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, evidence.RootElement.GetProperty("rightDiagnostic").ValueKind);
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldNotPersistPartialBatchWhenCandidateClassificationIsCancelled()
    {
        var repository = new InMemoryFontRepository();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var analyzer = new FontDuplicateAnalyzer(
            new StubEvidenceSource(Snapshot()),
            new FontRelationshipClassifier(),
            new FontRetentionSelector(),
            TimeProvider.System);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => analyzer.AnalyzeAsync(42, null, cancellation.Token));

        Assert.Null(await repository.GetLatestCompleteAnalysisAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldLoadDeepEvidenceOnlyForCandidateFiles()
    {
        var repository = new InMemoryFontRepository();
        FontFaceEvidence deepLeft = Evidence(1, 10, "left.ttf", 100) with
        {
            FileHash = Bytes("left-file"),
            StrictFingerprint = Bytes("left-face")
        };
        FontFaceEvidence deepRight = Evidence(2, 20, "right.ttf", 200) with
        {
            FileHash = Bytes("right-file"),
            StrictFingerprint = Bytes("right-face")
        };
        FontAnalysisSnapshot deepSnapshot = new(
            [deepLeft, deepRight],
            new DirectoryPolicySnapshot(
            [
                RetentionFile(10, "left.ttf", 100, 1),
                RetentionFile(20, "right.ttf", 200, 2)
            ]));
        FontAnalysisSnapshot lightweight = deepSnapshot with
        {
            Faces = deepSnapshot.Faces.Select(face => face with
            {
                IsDeepEvidenceComplete = false,
                UnicodeCoverage = [],
                CharacterFingerprints = new Dictionary<uint, ReadOnlyMemory<byte>>()
            }).ToArray()
        };
        var source = new LazyEvidenceSource(lightweight, deepSnapshot);
        var analyzer = new FontDuplicateAnalyzer(
            source,
            new FontRelationshipClassifier(),
            new FontRetentionSelector(),
            TimeProvider.System);

        AnalysisBatch result = await analyzer.AnalyzeAsync(42, null, CancellationToken.None);

        Assert.Equal([10L, 20L], source.LoadedFileIds);
        Assert.Equal(FontRelationshipKind.RenderingAndLayoutEquivalent, Assert.Single(result.Relations).RelationshipKind);
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldCoalesceSmallDisconnectedCandidateGroups()
    {
        var repository = new InMemoryFontRepository();
        FontFaceEvidence first = Evidence(1, 10, "first.ttf", 100);
        FontFaceEvidence second = Evidence(2, 20, "second.ttf", 100);
        FontFaceEvidence third = OtherFamily(Evidence(3, 30, "third.ttf", 100));
        FontFaceEvidence fourth = OtherFamily(Evidence(4, 40, "fourth.ttf", 100));
        FontAnalysisSnapshot deep = new(
            [first, second, third, fourth],
            new DirectoryPolicySnapshot(
            [
                RetentionFile(10, "first.ttf", 100, 1),
                RetentionFile(20, "second.ttf", 100, 2),
                RetentionFile(30, "third.ttf", 100, 3),
                RetentionFile(40, "fourth.ttf", 100, 4)
            ]));
        FontAnalysisSnapshot lightweight = deep with
        {
            Faces = deep.Faces.Select(face => face with
            {
                IsDeepEvidenceComplete = false,
                UnicodeCoverage = [],
                CharacterFingerprints = new Dictionary<uint, ReadOnlyMemory<byte>>()
            }).ToArray()
        };
        var source = new LazyEvidenceSource(lightweight, deep);
        var analyzer = new FontDuplicateAnalyzer(
            source,
            new FontRelationshipClassifier(),
            new FontRetentionSelector(),
            TimeProvider.System);

        AnalysisBatch result = await analyzer.AnalyzeAsync(42, null, CancellationToken.None);

        Assert.Single(source.LoadCalls);
        Assert.Equal([10L, 20L, 30L, 40L], source.LoadCalls[0]);
        Assert.Equal(2, result.Relations.Count);
    }

    [Fact]
    public async Task AnalyzeAsync_ShouldBoundDisconnectedCandidateBatchesTo64Files()
    {
        var repository = new InMemoryFontRepository();
        var deepFaces = new List<FontFaceEvidence>();
        var retentionFiles = new List<FontRetentionFile>();
        for (int group = 0; group < 33; group++)
        {
            string family = "GROUP" + group.ToString(System.Globalization.CultureInfo.InvariantCulture);
            for (int member = 0; member < 2; member++)
            {
                long id = (group * 2) + member + 1;
                string path = family + member.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".ttf";
                deepFaces.Add(GroupFace(Evidence(id, id, path, 100), family));
                retentionFiles.Add(RetentionFile(id, path, 100, id));
            }
        }

        FontAnalysisSnapshot deep = new(deepFaces, new DirectoryPolicySnapshot(retentionFiles));
        FontAnalysisSnapshot lightweight = deep with
        {
            Faces = deep.Faces.Select(face => face with
            {
                IsDeepEvidenceComplete = false,
                UnicodeCoverage = [],
                CharacterFingerprints = new Dictionary<uint, ReadOnlyMemory<byte>>()
            }).ToArray()
        };
        var source = new LazyEvidenceSource(lightweight, deep);
        var analyzer = new FontDuplicateAnalyzer(
            source,
            new FontRelationshipClassifier(),
            new FontRetentionSelector(),
            TimeProvider.System);
        var progress = new InlineProgress();

        AnalysisBatch result = await analyzer.AnalyzeAsync(42, progress, CancellationToken.None);

        Assert.Contains(progress.Values, value => value is > 0 and < 100);
        Assert.Equal(100, progress.Values[^1]);
        Assert.Equal(2, source.LoadCalls.Count);
        Assert.Equal(64, source.LoadCalls[0].Count);
        Assert.Equal(2, source.LoadCalls[1].Count);
        Assert.Equal(33, result.Relations.Count);
    }

    private static FontFaceEvidence GroupFace(FontFaceEvidence face, string family)
        => face with
        {
            FileHash = Bytes(family + "-file"),
            StrictFingerprint = Bytes(family + "-face"),
            Identity = face.Identity with
            {
                PostScriptName = family + "-REGULAR",
                FamilyName = family,
                FullName = family + " REGULAR",
                UniqueId = "TEST:" + family,
                PrimaryIdentity = family + "-REGULAR",
                AllNames = new HashSet<string> { family, family + " REGULAR", family + "-REGULAR" }
            }
        };
    private static FontFaceEvidence OtherFamily(FontFaceEvidence face)
        => face with
        {
            FileHash = Bytes("other-file"),
            StrictFingerprint = Bytes("other-face"),
            Identity = face.Identity with
            {
                PostScriptName = "OTHER-REGULAR",
                FamilyName = "OTHER",
                FullName = "OTHER REGULAR",
                UniqueId = "TEST:OTHER",
                PrimaryIdentity = "OTHER-REGULAR",
                AllNames = new HashSet<string> { "OTHER", "OTHER REGULAR", "OTHER-REGULAR" }
            }
        };
    private static FontAnalysisSnapshot Snapshot()
    {
        FontFaceEvidence left = Evidence(1, 10, "left.ttf", 100);
        FontFaceEvidence right = Evidence(2, 20, "right.ttf", 200);
        return new FontAnalysisSnapshot(
            [left, right],
            new DirectoryPolicySnapshot(
            [
                RetentionFile(10, "left.ttf", 100, 1),
                RetentionFile(20, "right.ttf", 200, 2)
            ]));
    }

    private static FontFaceEvidence Evidence(long faceId, long fileId, string path, long size)
    {
        ReadOnlyMemory<byte> glyph = Bytes("glyph");
        return new FontFaceEvidence
        {
            FaceId = faceId,
            FileId = fileId,
            IsClassifiable = true,
            IsDeepEvidenceComplete = true,
            PhysicalFileId = path,
            FaceIndex = 0,
            FileHash = Bytes("identical-file"),
            StrictFingerprint = Bytes("identical-face"),
            SemanticFingerprints = new Dictionary<FontSemanticGroup, ReadOnlyMemory<byte>>(),
            Identity = new FontIdentityEvidence(
                "EXAMPLE-REGULAR",
                "EXAMPLE",
                "REGULAR",
                16,
                17,
                "EXAMPLE REGULAR",
                "TEST:EXAMPLE",
                "EXAMPLE-REGULAR",
                new HashSet<string> { "EXAMPLE", "EXAMPLE REGULAR", "EXAMPLE-REGULAR" },
                "TEST",
                400,
                5,
                0,
                new FontVersionEvidence(1m, true, ["head.fontRevision", "name.5"])),
            Technology = "TrueType",
            UnitsPerEm = 1000,
            UnicodeCoverage = [new UnicodeRange(0x41, 0x41)],
            CharacterFingerprints = new Dictionary<uint, ReadOnlyMemory<byte>> { [0x41] = glyph }
        };
    }

    private static FontRetentionFile RetentionFile(long fileId, string path, long size, long faceId)
    {
        return new FontRetentionFile
        {
            FileId = fileId,
            Path = path,
            NormalizedPath = path.ToUpperInvariant(),
            Size = size,
            ModifiedUtc = DateTimeOffset.UnixEpoch,
            DirectoryPolicy = FontDirectoryPolicy.None,
            LinkKind = FontLinkKind.None,
            Health = FontFileHealth.Healthy,
            Faces =
            [
                new FontRetentionFace(
                    faceId,
                    "TrueType",
                    true,
                    new FontRetentionQualityEvidence(Enum.GetValues<FontQualitySignal>().ToHashSet(), 1m))
            ]
        };
    }

    private static ReadOnlyMemory<byte> Bytes(string value) => Encoding.UTF8.GetBytes(value);

    private sealed class StubEvidenceSource(FontAnalysisSnapshot snapshot) : IFontAnalysisEvidenceSource
    {
        public Task<FontAnalysisSnapshot> LoadAsync(long generationId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(snapshot);
        }
    }

    private sealed class LazyEvidenceSource(
        FontAnalysisSnapshot lightweight,
        FontAnalysisSnapshot deep) : IFontAnalysisEvidenceSource, IFontDeepAnalysisEvidenceSource
    {
        public IReadOnlyList<long> LoadedFileIds { get; private set; } = [];

        public List<IReadOnlyList<long>> LoadCalls { get; } = [];

        public Task<FontAnalysisSnapshot> LoadAsync(long generationId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(lightweight);
        }

        public Task<FontDeepAnalysisEvidence> LoadFilesAsync(
            long generationId,
            IReadOnlyList<long> fileIds,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadedFileIds = fileIds.ToArray();
            LoadCalls.Add(LoadedFileIds);
            HashSet<long> requested = fileIds.ToHashSet();
            return Task.FromResult(new FontDeepAnalysisEvidence(
                deep.Faces.Where(face => requested.Contains(face.FileId)).ToArray(),
                deep.Policy.Files.Where(file => requested.Contains(file.FileId)).ToArray()));
        }
    }

    private sealed class InlineProgress : IProgress<double>
    {
        public List<double> Values { get; } = [];

        public void Report(double value) => Values.Add(value);
    }
    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
