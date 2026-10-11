using Jellyfin.Plugin.AssSubsetter.Fonts.Api;
using Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;
using Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Persistence;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Api;

/// <summary>Verifies administrator font read models are safe, bounded, and derived from persisted records.</summary>
public sealed class FontManagementQueryTests
{
    [Fact]
    public async Task AnalysisValidityExplainsConfigurationChangeBeforeNextScanAndSupersededPublication()
    {
        var repository = new InMemoryFontRepository();
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "analysis-validity"));
        string? preferred = null;
        Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontDirectoryPolicyResolver Policy() => new(root, null, preferred, false);
        var input = Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontInventoryInputComparer.Capture([], Policy(), PhysicalFontAnalysisEvidenceSource.AnalysisCompatibilityKey);
        long generation = await repository.BeginGenerationAsync(input, [], CancellationToken.None);
        var batch = await repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = generation, Status = AnalysisBatchStatus.Complete, EvidenceVersion = FontDuplicateAnalyzer.CurrentEvidenceVersion, SummaryJson = "{}" }, CancellationToken.None);
        var query = new FontManagementQuery(repository, Policy);
        Assert.True((await query.GetAnalysisBatchesAsync(1, 50, CancellationToken.None)).Items[0].IsCurrent);
        preferred = root;
        var changed = (await query.GetAnalysisBatchesAsync(1, 50, CancellationToken.None)).Items[0];
        Assert.False(changed.IsCurrent);
        Assert.Contains("directory-policy-changed", changed.StaleReasons);
        long next = await repository.BeginGenerationAsync(input, [], CancellationToken.None);
        await repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = next, Status = AnalysisBatchStatus.Complete, EvidenceVersion = FontDuplicateAnalyzer.CurrentEvidenceVersion, SummaryJson = "{}" }, CancellationToken.None);
        var old = (await query.GetAnalysisBatchesAsync(1, 50, CancellationToken.None)).Items.Single(item => item.Id == batch.Id);
        Assert.Contains("generation-superseded", old.StaleReasons);
        Assert.Contains("analysis-superseded", old.StaleReasons);
    }

    [Fact]
    public async Task GetReportsAsync_ShouldExposeExplainableReportOnlyPairAndEveryScoreComponent()
    {
        var repository = new InMemoryFontRepository();
        long generation = await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await repository.WriteInventoryAsync(generation, [File(1, generation, "/custom/collection.ttc", FontFileHealth.Healthy, Face(10, "TrueType"), Face(11, "TrueType")), File(2, generation, "/custom/single.ttf", FontFileHealth.Healthy, Face(20, "TrueType"))], CancellationToken.None);

        AnalysisBatch batch = await repository.PublishAnalysisAsync(new AnalysisBatch
        {
            GenerationId = generation,
            Status = AnalysisBatchStatus.Complete,
            EvidenceVersion = FontDuplicateAnalyzer.CurrentEvidenceVersion,
            StartedUtc = DateTimeOffset.UnixEpoch,
            CompletedUtc = DateTimeOffset.UnixEpoch,
            SummaryJson = "{}",
            Relations = [new FontRelationRecord { LeftFaceId = 10, RightFaceId = 20, RelationshipKind = FontRelationshipKind.SmallGlyphDifference, ReplacementDirection = FontReplacementDirection.None, EvidenceVersion = FontDuplicateAnalyzer.CurrentEvidenceVersion, EvidenceJson = "{\"evidenceKeys\":[\"glyphs.bounded-difference\"],\"differingCodePoints\":[65],\"leftCanReplaceRight\":false,\"rightCanReplaceLeft\":false}" }],
            FaceScores = [new FontFaceAnalysisRecord { FaceId = 10, FileId = 1, RetentionScore = 15, ScoreJson = "{\"content\":1,\"version\":2,\"technical\":3,\"names\":4,\"modified\":5}", VerificationJson = "{\"skia\":1,\"freetype\":0}" }]
        }, CancellationToken.None);

        PagedResult<FontReportItemDto> page = await new FontManagementQuery(repository).GetReportsAsync(new FontReportQuery(1, 10, batch.Id, null, "all", null, null, null), CancellationToken.None);

        FontReportItemDto row = Assert.Single(page.Items);
        Assert.Equal(FontReportAction.ReportOnly, row.Action);
        Assert.Equal([65U], row.PairEvidence!.DifferingCodePoints);
        Assert.Equal(["SmallGlyphDifference"], row.PairEvidence.DifferenceTypes);
        Assert.Contains("2 Faces", row.PairEvidence.CollectionFaceImpact, StringComparison.Ordinal);
        FontReportScoreDto score = Assert.Single(row.Scores);
        Assert.Equal(new RetentionScoreBreakdownDto(1, 2, 3, 4, 5), score.Components);
        Assert.Equal(FontVerificationStatus.Unknown, score.Verification["freetype"]);
    }

    [Fact]
    public async Task GetReportsAsync_ShouldExplainBothSidesOfANameConflictAndIncompleteEvidence()
    {
        var repository = new InMemoryFontRepository();
        long generation = await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        FontFaceRecord bold = Face(10, "TrueType") with
        {
            FamilyName = "Agency FB",
            StyleName = "Bold",
            Weight = 700,
            Names = ["Agency FB", "Agency FB Bold"],
            Version = "1.0",
            StrictFingerprint = new byte[] { 1, 2 },
            NameRecords = [new(3, 1, 0x0409, 6, "AgencyFB-Bold")]
        };
        FontFaceRecord regular = Face(20, "TrueType") with
        {
            FamilyName = "Agency FB",
            StyleName = "Regular",
            Weight = 400,
            Names = ["Agency FB", "Agency FB Regular"],
            Version = "2.0",
            StrictFingerprint = new byte[] { 3, 4 },
            NameRecords = [new(3, 1, 0x0409, 6, "AgencyFB-Regular")]
        };
        await repository.WriteInventoryAsync(generation, [
            File(1, generation, "/fonts/z-bold.ttf", FontFileHealth.Healthy, bold),
            File(2, generation, "/fonts/a-regular.ttf", FontFileHealth.Healthy, regular)
        ], CancellationToken.None);

        AnalysisBatch batch = await repository.PublishAnalysisAsync(new AnalysisBatch
        {
            GenerationId = generation,
            Status = AnalysisBatchStatus.Complete,
            StartedUtc = DateTimeOffset.UnixEpoch,
            CompletedUtc = DateTimeOffset.UnixEpoch,
            SummaryJson = "{}",
            Relations = [new FontRelationRecord
            {
                LeftFaceId = 10,
                RightFaceId = 20,
                RelationshipKind = FontRelationshipKind.Unclassifiable,
                EvidenceJson = "{\"evidenceKeys\":[\"classification.incomplete\"],\"leftDiagnostic\":\"Glyph 98: unsupported flag\"}"
            }]
        }, CancellationToken.None);

        FontReportItemDto row = Assert.Single((await new FontManagementQuery(repository)
            .GetReportsAsync(new FontReportQuery(1, 10, batch.Id, null, "all", null, null, null), CancellationToken.None)).Items);

        Assert.Equal("/fonts/z-bold.ttf", row.PairEvidence!.LeftFace!.Path);
        Assert.Equal("Bold", row.PairEvidence.LeftFace.Style);
        Assert.Equal("AgencyFB-Bold", row.PairEvidence.LeftFace.PostScriptName);
        Assert.Equal("/fonts/a-regular.ttf", row.PairEvidence.RightFace!.Path);
        Assert.Equal("Regular", row.PairEvidence.RightFace.Style);
        Assert.Equal(["Agency FB"], row.PairEvidence.SharedNames);
        Assert.Contains("Glyph 98", row.PairEvidence.LeftDiagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetOverviewAsync_ShouldNotAdvertiseEligibilityFromASupersededGeneration()
    {
        var repository = new InMemoryFontRepository();
        long generationId = await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);

        await repository.PublishAnalysisAsync(new AnalysisBatch
        {
            GenerationId = generationId,
            Status = AnalysisBatchStatus.Complete,
            StartedUtc = DateTimeOffset.UnixEpoch,
            ReclaimableBytes = 512,
            SummaryJson = "{}",
            Groups = [new DuplicateGroupRecord { RetainedFaceId = 10, EvidenceJson = "{}", Members = [Member(1, 10, DuplicateGroupMemberDecision.QuarantineCandidate)] }]
        }, CancellationToken.None);

        long currentGeneration = await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = currentGeneration, Status = AnalysisBatchStatus.Complete, SummaryJson = "{}" }, CancellationToken.None);
        FontOverviewDto result = await new FontManagementQuery(repository).GetOverviewAsync(CancellationToken.None);

        Assert.Equal(0, result.EligibleQuarantineCount);
        Assert.Equal(0, result.ReclaimableBytes);
        Assert.Equal(currentGeneration, result.LatestAnalysis!.GenerationId);
    }

    [Theory]
    [InlineData(FontRelationshipKind.CoverageVariant, FontReportAction.ReportOnly)]
    [InlineData(FontRelationshipKind.SmallGlyphDifference, FontReportAction.ReportOnly)]
    [InlineData(FontRelationshipKind.LayoutOnlyDifference, FontReportAction.ReportOnly)]
    [InlineData(FontRelationshipKind.HintingOnlyDifference, FontReportAction.ReportOnly)]
    [InlineData(FontRelationshipKind.VersionConflict, FontReportAction.Conflict)]
    [InlineData(FontRelationshipKind.SameIdentityContentConflict, FontReportAction.Conflict)]
    [InlineData(FontRelationshipKind.PostScriptNameConflict, FontReportAction.Conflict)]
    [InlineData(FontRelationshipKind.CrossNameConflict, FontReportAction.Conflict)]
    [InlineData(FontRelationshipKind.Independent, FontReportAction.Retain)]
    [InlineData(FontRelationshipKind.Unclassifiable, FontReportAction.Retain)]
    public async Task GetReportsAsync_ShouldPreserveRelationshipActionBoundariesWhenAFaceIsACandidateElsewhere(FontRelationshipKind relationship, FontReportAction action)
    {
        var repository = new InMemoryFontRepository();
        long generationId = await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);

        AnalysisBatch batch = await repository.PublishAnalysisAsync(new AnalysisBatch
        {
            GenerationId = generationId,
            Status = AnalysisBatchStatus.Complete,
            StartedUtc = DateTimeOffset.UnixEpoch,
            SummaryJson = "{}",
            Relations = [new FontRelationRecord { LeftFaceId = 10, RightFaceId = 20, RelationshipKind = relationship, EvidenceJson = "{}" }],
            Groups = [new DuplicateGroupRecord { RetainedFaceId = 30, EvidenceJson = "{}", Members = [Member(1, 10, DuplicateGroupMemberDecision.QuarantineCandidate)] }]
        }, CancellationToken.None);

        PagedResult<FontReportItemDto> result = await new FontManagementQuery(repository).GetReportsAsync(
            new FontReportQuery(1, 50, batch.Id, relationship.ToString(), null, null, null, action.ToString()), CancellationToken.None);

        Assert.Equal(action, Assert.Single(result.Items).Action);
    }

    [Fact]
    public async Task GetOverviewAsync_ShouldExposeHealthGenerationEligibilityReclaimableBytesAndPhaseTimings()
    {
        var repository = new InMemoryFontRepository();
        long generationId = await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await repository.WriteInventoryAsync(generationId, [File(1, generationId, "/custom/a.ttf", FontFileHealth.Healthy, Face(10, "TrueType"))], CancellationToken.None);

        await repository.PublishAnalysisAsync(new AnalysisBatch
        {
            GenerationId = generationId,
            Status = AnalysisBatchStatus.Complete,
            EvidenceVersion = 1,
            StartedUtc = DateTimeOffset.UnixEpoch,
            CompletedUtc = DateTimeOffset.UnixEpoch.AddSeconds(1),
            ReclaimableBytes = 512,
            SummaryJson = "{}",
            Groups =
            [
                new DuplicateGroupRecord
                {
                    RetainedFaceId = 20,
                    ReclaimableBytes = 512,
                    EvidenceJson = "{}",
                    Members = [Member(1, 10, DuplicateGroupMemberDecision.QuarantineCandidate)]
                }
            ]
        }, CancellationToken.None);
        await repository.SavePerformanceSampleAsync(new PerformanceSample
        {
            GenerationId = generationId,
            Phase = "inventory",
            Duration = TimeSpan.FromMilliseconds(25),
            ItemCount = 1,
            RecordedUtc = DateTimeOffset.UnixEpoch
        }, CancellationToken.None);
        var query = new FontManagementQuery(repository);

        FontOverviewDto result = await query.GetOverviewAsync(CancellationToken.None);

        Assert.Equal(generationId, result.PublishedGenerationId);
        Assert.Equal(1, result.EligibleQuarantineCount);
        Assert.Equal(512, result.ReclaimableBytes);
        Assert.Equal(SqliteSchema.CurrentVersion, result.DatabaseHealth.SchemaVersion);
        Assert.Equal("inventory", Assert.Single(result.PhaseTimings).Phase);
    }

    [Fact]
    public async Task GetReportsAsync_ShouldShowOneReplacementPlanAndPreserveConflictsBetweenItsMembers()
    {
        var repository = new InMemoryFontRepository();
        long generation = await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await repository.WriteInventoryAsync(generation, [
            File(1, generation, "/fonts/keep.ttf", FontFileHealth.Healthy, Face(10, "TrueType")),
            File(2, generation, "/fonts/copy.ttf", FontFileHealth.Healthy, Face(20, "TrueType")),
            File(3, generation, "/fonts/older.ttf", FontFileHealth.Healthy, Face(30, "TrueType"))
        ], CancellationToken.None);

        AnalysisBatch batch = await repository.PublishAnalysisAsync(new AnalysisBatch
        {
            GenerationId = generation,
            Status = AnalysisBatchStatus.Complete,
            StartedUtc = DateTimeOffset.UnixEpoch,
            SummaryJson = "{}",
            Relations = [
                new FontRelationRecord { Id = 1, LeftFaceId = 10, RightFaceId = 20, RelationshipKind = FontRelationshipKind.StrictFaceDuplicate, ReplacementDirection = FontReplacementDirection.Both, EvidenceJson = "{}" },
                new FontRelationRecord { Id = 2, LeftFaceId = 10, RightFaceId = 30, RelationshipKind = FontRelationshipKind.MetadataOnlyDifference, ReplacementDirection = FontReplacementDirection.Both, EvidenceJson = "{}" },
                new FontRelationRecord { Id = 3, LeftFaceId = 20, RightFaceId = 30, RelationshipKind = FontRelationshipKind.VersionConflict, EvidenceJson = "{}" }
            ],
            Groups = [new DuplicateGroupRecord
            {
                Id = 5, RetainedFaceId = 10, ReclaimableBytes = 2048, EvidenceJson = "{}",
                Members = [Member(1, 10, DuplicateGroupMemberDecision.Retained), Member(2, 20, DuplicateGroupMemberDecision.QuarantineCandidate), Member(3, 30, DuplicateGroupMemberDecision.QuarantineCandidate)]
            }]
        }, CancellationToken.None);
        var query = new FontManagementQuery(repository);

        PagedResult<FontReportItemDto> page = await query.GetReportsAsync(new FontReportQuery(1, 50, batch.Id, null, "all", null, null, null), CancellationToken.None);
        PagedResult<FontReportItemDto> filtered = await query.GetReportsAsync(new FontReportQuery(1, 50, batch.Id, "metadataOnlyDifference", "all", "/fonts", "TrueType", "quarantineCandidate"), CancellationToken.None);

        Assert.Equal(2, page.TotalCount);
        FontReportItemDto plan = Assert.Single(page.Items, item => item.Action == FontReportAction.QuarantineCandidate);
        FontReportReplacementDto replacement = Assert.IsType<FontReportReplacementDto>(plan.Replacement);
        Assert.Equal(5, replacement.GroupId);
        Assert.Equal(2048, replacement.ReclaimableBytes);
        Assert.Equal([FontRelationshipKind.StrictFaceDuplicate, FontRelationshipKind.MetadataOnlyDifference], replacement.RelationshipKinds);
        Assert.Equal([2L, 3L], replacement.Files.Where(file => file.Decision == DuplicateGroupMemberDecision.QuarantineCandidate).Select(file => file.FileId));
        Assert.Equal("/fonts/keep.ttf", Assert.Single(replacement.Files, file => file.Decision == DuplicateGroupMemberDecision.Retained).Path);
        Assert.Single(page.Items, item => item.Action == FontReportAction.Conflict && item.RelationId == 3);
        Assert.Single(filtered.Items);
    }
    [Fact]
    public async Task GetReportsAsync_ShouldNotOfferPartialCollectionAsAWholeFileReplacement()
    {
        var repository = new InMemoryFontRepository();
        long generation = await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await repository.WriteInventoryAsync(generation, [
            File(1, generation, "/fonts/keep.ttf", FontFileHealth.Healthy, Face(10, "TrueType")),
            File(2, generation, "/fonts/collection.ttc", FontFileHealth.Healthy, Face(20, "TrueType"), Face(21, "TrueType"))
        ], CancellationToken.None);
        AnalysisBatch batch = await repository.PublishAnalysisAsync(new AnalysisBatch
        {
            GenerationId = generation, Status = AnalysisBatchStatus.Complete, StartedUtc = DateTimeOffset.UnixEpoch, SummaryJson = "{}",
            Relations = [new FontRelationRecord { LeftFaceId = 10, RightFaceId = 20, RelationshipKind = FontRelationshipKind.StrictFaceDuplicate, ReplacementDirection = FontReplacementDirection.Both, EvidenceJson = "{}" }],
            Groups = [new DuplicateGroupRecord { Id = 5, RetainedFaceId = 10, EvidenceJson = "{}", Members = [Member(1, 10, DuplicateGroupMemberDecision.Retained), Member(2, 20, DuplicateGroupMemberDecision.QuarantineCandidate)] }]
        }, CancellationToken.None);

        FontReportItemDto row = Assert.Single((await new FontManagementQuery(repository).GetReportsAsync(new FontReportQuery(1, 50, batch.Id, null, "all", null, null, null), CancellationToken.None)).Items);

        Assert.Null(row.Replacement);
        Assert.NotNull(row.PairEvidence);
        Assert.Contains("2 Faces", row.PairEvidence.CollectionFaceImpact, StringComparison.Ordinal);
    }
    [Fact]
    public async Task GetReportsAsync_ShouldApplyAllAllowlistedFiltersAndBoundPagination()
    {
        var repository = new InMemoryFontRepository();
        long generationId = await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        FontFileRecord left = File(1, generationId, "/custom/alpha.ttf", FontFileHealth.Healthy, Face(10, "TrueType"));
        FontFileRecord right = File(2, generationId, "/system/beta.otf", FontFileHealth.Healthy, Face(20, "OpenType CFF"));
        FontFileRecord broken = File(3, generationId, "/custom/broken.ttf", FontFileHealth.Unclassifiable);
        await repository.WriteInventoryAsync(generationId, [left, right, broken], CancellationToken.None);

        AnalysisBatch batch = await repository.PublishAnalysisAsync(new AnalysisBatch
        {
            GenerationId = generationId,
            Status = AnalysisBatchStatus.Complete,
            EvidenceVersion = 1,
            StartedUtc = DateTimeOffset.UnixEpoch,
            CompletedUtc = DateTimeOffset.UnixEpoch,
            SummaryJson = "{}",
            Relations =
            [
                new FontRelationRecord
                {
                    Id = 99,
                    LeftFaceId = 10,
                    RightFaceId = 20,
                    RelationshipKind = FontRelationshipKind.StrictFaceDuplicate,
                    ReplacementDirection = FontReplacementDirection.Both,
                    EvidenceVersion = 1,
                    EvidenceJson = "{}"
                }
            ],
            Groups =
            [
                new DuplicateGroupRecord
                {
                    RetainedFaceId = 20,
                    EvidenceJson = "{}",
                    Members =
                    [
                        Member(1, 10, DuplicateGroupMemberDecision.QuarantineCandidate),
                        Member(2, 20, DuplicateGroupMemberDecision.Retained)
                    ]
                }
            ]
        }, CancellationToken.None);
        var query = new FontManagementQuery(repository);

        PagedResult<FontReportItemDto> relationships = await query.GetReportsAsync(
            new FontReportQuery(1, 500, batch.Id, "strictFaceDuplicate", "exclude", "/custom", "TrueType", "quarantineCandidate"),
            CancellationToken.None);
        PagedResult<FontReportItemDto> unclassifiable = await query.GetReportsAsync(
            new FontReportQuery(1, 50, batch.Id, null, "only", "/custom", null, "retain"),
            CancellationToken.None);

        FontReportItemDto relationship = Assert.Single(relationships.Items);
        Assert.Equal(200, relationships.PageSize);
        Assert.Equal(FontRelationshipKind.StrictFaceDuplicate, relationship.RelationshipKind);
        Assert.Equal(FontReportAction.QuarantineCandidate, relationship.Action);
        FontReportItemDto unclassifiableItem = Assert.Single(unclassifiable.Items);
        Assert.True(unclassifiableItem.IsUnclassifiable);
        Assert.Equal([3L], unclassifiableItem.FileIds);
    }

    [Theory]
    [InlineData("not-a-relationship", null, null)]
    [InlineData(null, "maybe", null)]
    [InlineData(null, null, "delete")]
    public async Task GetReportsAsync_ShouldRejectNonAllowlistedEnumFilters(string? relationship, string? unclassifiable, string? action)
    {
        var query = new FontManagementQuery(new InMemoryFontRepository());

        await Assert.ThrowsAsync<ArgumentException>(() => query.GetReportsAsync(
            new FontReportQuery(1, 50, null, relationship, unclassifiable, null, null, action),
            CancellationToken.None));
    }

    [Fact]
    public async Task GetGroupsAsync_ShouldClampPageSizeToTwoHundred()
    {
        var repository = new InMemoryFontRepository();
        var query = new FontManagementQuery(repository);

        PagedResult<FontGroupDto> result = await query.GetGroupsAsync(
            new FontGroupQuery(1, 1000, null, null, null),
            CancellationToken.None);

        Assert.Equal(200, result.PageSize);
    }

    [Fact]
    public async Task GetGroupsAsync_ShouldNotExposePersistedEvidenceDocuments()
    {
        var repository = new InMemoryFontRepository();
        await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        AnalysisBatch batch = await repository.PublishAnalysisAsync(new AnalysisBatch
        {
            GenerationId = 1,
            Status = AnalysisBatchStatus.Complete,
            EvidenceVersion = 1,
            StartedUtc = DateTimeOffset.UtcNow,
            CompletedUtc = DateTimeOffset.UtcNow,
            SummaryJson = "{\"secret\":true}",
            Groups =
            [
                new DuplicateGroupRecord
                {
                    Id = 5,
                    RetainedFaceId = 11,
                    ReclaimableBytes = 99,
                    EvidenceJson = "{\"raw\":true}",
                    Members =
                    [
                        new DuplicateGroupMemberRecord
                        {
                            Id = 7,
                            FaceId = 11,
                            FileId = 13,
                            Decision = DuplicateGroupMemberDecision.Retained,
                            RetentionScore = 90,
                            ScoreJson = "{\"raw\":true}",
                            Reason = "retained"
                        }
                    ]
                }
            ]
        }, CancellationToken.None);
        var query = new FontManagementQuery(repository);

        PagedResult<FontGroupDto> result = await query.GetGroupsAsync(
            new FontGroupQuery(1, 10, batch.Id, null, null),
            CancellationToken.None);

        FontGroupDto group = Assert.Single(result.Items);
        Assert.Equal(5, group.Id);
        Assert.DoesNotContain("EvidenceJson", typeof(FontGroupDto).GetProperties().Select(property => property.Name));
        Assert.DoesNotContain("ScoreJson", typeof(FontGroupDto).GetProperties().Select(property => property.Name));
    }

    [Fact]
    public async Task GetAnalysisBatchesAsync_ShouldPageBeyondTheFirstThousandWithAnAccurateTotal()
    {
        var repository = new InMemoryFontRepository();
        for (int index = 0; index < 1001; index++)
        {
            long generation = await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
            await repository.PublishAnalysisAsync(new AnalysisBatch
            {
                GenerationId = generation,
                Status = AnalysisBatchStatus.Complete,
                EvidenceVersion = 1,
                StartedUtc = DateTimeOffset.UnixEpoch.AddMinutes(index),
                CompletedUtc = DateTimeOffset.UnixEpoch.AddMinutes(index),
                SummaryJson = "{}"
            }, CancellationToken.None);
        }

        var query = new FontManagementQuery(repository);
        PagedResult<AnalysisBatchDto> result = await query.GetAnalysisBatchesAsync(6, 200, CancellationToken.None);

        Assert.Equal(1001, result.TotalCount);
        Assert.Equal(1, Assert.Single(result.Items).Id);
    }

    [Fact]
    public async Task GetGroupDetailAsync_ShouldTreatNonObjectEvidenceAndScoresAsSafeEmptyData()
    {
        var repository = new InMemoryFontRepository();
        await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        AnalysisBatch batch = await repository.PublishAnalysisAsync(new AnalysisBatch
        {
            GenerationId = 1,
            Status = AnalysisBatchStatus.Complete,
            EvidenceVersion = 1,
            StartedUtc = DateTimeOffset.UtcNow,
            CompletedUtc = DateTimeOffset.UtcNow,
            SummaryJson = "[]",
            Relations =
            [
                new FontRelationRecord
                {
                    LeftFaceId = 10,
                    RightFaceId = 20,
                    EvidenceVersion = 1,
                    EvidenceJson = "[]"
                }
            ],
            Groups =
            [
                new DuplicateGroupRecord
                {
                    Id = 5,
                    RetainedFaceId = 10,
                    EvidenceJson = "[]",
                    Members =
                    [
                        new DuplicateGroupMemberRecord
                        {
                            FaceId = 10,
                            FileId = 1,
                            Decision = DuplicateGroupMemberDecision.Retained,
                            ScoreJson = "[]",
                            Reason = "retained"
                        },
                        new DuplicateGroupMemberRecord
                        {
                            FaceId = 20,
                            FileId = 2,
                            Decision = DuplicateGroupMemberDecision.QuarantineCandidate,
                            ScoreJson = "[]",
                            Reason = "replaced"
                        }
                    ]
                }
            ]
        }, CancellationToken.None);
        var query = new FontManagementQuery(repository);

        FontGroupDetailDto detail = Assert.IsType<FontGroupDetailDto>(await query.GetGroupDetailAsync(batch.Id, 5, CancellationToken.None));

        PairEvidenceDto pair = Assert.Single(detail.Pairs);
        Assert.Empty(pair.EvidenceKeys);
        Assert.Empty(pair.DifferingCodePoints);
        FontGroupMemberDto member = Assert.Single(detail.Group.Members, candidate => candidate.FaceId == 10);
        Assert.Equal(new RetentionScoreBreakdownDto(0, 0, 0, 0, 0), member.Score);
    }

    [Fact]
    public async Task GetGroupDetailAsync_ShouldIncludeOnlyRelationsBetweenGroupMembers()
    {
        var repository = new InMemoryFontRepository();
        long generationId = await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await repository.WriteInventoryAsync(generationId,
        [
            File(1, generationId, "/custom/alpha.ttc", FontFileHealth.Healthy, Face(10, "TrueType")),
            File(2, generationId, "/custom/beta.otf", FontFileHealth.Healthy, Face(20, "OpenType CFF")),
            File(3, generationId, "/custom/other.otf", FontFileHealth.Healthy, Face(30, "OpenType CFF"))
        ], CancellationToken.None);
        AnalysisBatch batch = await repository.PublishAnalysisAsync(new AnalysisBatch
        {
            GenerationId = generationId,
            Status = AnalysisBatchStatus.Complete,
            EvidenceVersion = 1,
            StartedUtc = DateTimeOffset.UtcNow,
            CompletedUtc = DateTimeOffset.UtcNow,
            SummaryJson = "{}",
            Relations =
            [
                new FontRelationRecord { LeftFaceId = 10, RightFaceId = 20, EvidenceVersion = 1, EvidenceJson = "{}" },
                new FontRelationRecord { LeftFaceId = 10, RightFaceId = 30, EvidenceVersion = 1, EvidenceJson = "{}" }
            ],
            Groups =
            [
                new DuplicateGroupRecord
                {
                    Id = 5,
                    RetainedFaceId = 10,
                    EvidenceJson = "{}",
                    Members =
                    [
                        new DuplicateGroupMemberRecord { FaceId = 10, FileId = 1, Decision = DuplicateGroupMemberDecision.Retained, ScoreJson = "{}", Reason = "retained" },
                        new DuplicateGroupMemberRecord { FaceId = 20, FileId = 2, Decision = DuplicateGroupMemberDecision.QuarantineCandidate, ScoreJson = "{}", Reason = "replaced" }
                    ]
                }
            ]
        }, CancellationToken.None);

        var query = new FontManagementQuery(repository);
        FontGroupDetailDto detail = Assert.IsType<FontGroupDetailDto>(await query.GetGroupDetailAsync(batch.Id, 5, CancellationToken.None));

        PairEvidenceDto pair = Assert.Single(detail.Pairs);
        Assert.Equal(10, pair.LeftFaceId);
        Assert.Equal(20, pair.RightFaceId);
        Assert.Equal("/custom/alpha.ttc", Assert.Single(detail.Group.Members, member => member.FaceId == 10).Face?.Path);
        Assert.Equal("/custom/beta.otf", Assert.Single(detail.Group.Members, member => member.FaceId == 20).Face?.Path);
    }

    [Fact]
    public async Task GetQuarantineItemsAsync_ShouldExposeOriginalAndQuarantinePathsForReview()
    {
        var repository = new InMemoryFontRepository();
        MaintenanceBatch batch = await repository.SaveMaintenanceBatchAsync(new MaintenanceBatch
        {
            OperationRequestId = 1,
            Kind = FontOperationKind.Quarantine,
            Status = MaintenanceBatchStatus.Partial,
            StartedUtc = DateTimeOffset.UnixEpoch,
            SummaryJson = "{}",
            Items =
            [
                new QuarantineItemRecord
                {
                    FileId = 7,
                    SourcePath = "/custom/fonts/Example Bold.otf",
                    StagingPath = "/quarantine/staging/7.font",
                    QuarantinePath = "/quarantine/items/7.font",
                    Sha256 = new byte[] { 1 },
                    Status = QuarantineItemStatus.FailedIo,
                    Error = "permission-denied"
                }
            ]
        }, CancellationToken.None);
        var query = new FontManagementQuery(repository);

        QuarantineItemDto item = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<QuarantineItemDto>>(
            await query.GetQuarantineItemsAsync(batch.Id, CancellationToken.None)));

        Assert.Equal("/custom/fonts/Example Bold.otf", item.SourcePath);
        Assert.Equal("/quarantine/items/7.font", item.QuarantinePath);
        Assert.Equal(QuarantineItemStatus.FailedIo, item.Status);
        Assert.Equal(batch.Id, item.MaintenanceBatchId);
        PagedResult<QuarantineItemDto> failed = await query.GetQuarantineItemsPageAsync(1, 50, "failed", CancellationToken.None);
        Assert.Equal(batch.Id, Assert.Single(failed.Items).MaintenanceBatchId);
        Assert.Empty((await query.GetQuarantineItemsPageAsync(1, 50, "pending", CancellationToken.None)).Items);
        await Assert.ThrowsAsync<ArgumentException>(() => query.GetQuarantineItemsPageAsync(1, 50, "unknown", CancellationToken.None));
    }

    [Fact]
    public async Task GetQuarantineItemsAsync_ShouldRejectNonQuarantineMaintenanceBatch()
    {
        var repository = new InMemoryFontRepository();
        MaintenanceBatch batch = await repository.SaveMaintenanceBatchAsync(new MaintenanceBatch
        {
            OperationRequestId = 1,
            Kind = FontOperationKind.Restore,
            Status = MaintenanceBatchStatus.Complete,
            StartedUtc = DateTimeOffset.UnixEpoch,
            SummaryJson = "{}",
            Items =
            [
                new QuarantineItemRecord
                {
                    FileId = 1,
                    SourcePath = "source.ttf",
                    StagingPath = "staging.ttf",
                    QuarantinePath = "quarantine.ttf",
                    Sha256 = new byte[] { 1 }
                }
            ]
        }, CancellationToken.None);
        var query = new FontManagementQuery(repository);

        IReadOnlyList<QuarantineItemDto>? result = await query.GetQuarantineItemsAsync(batch.Id, CancellationToken.None);

        Assert.Null(result);
    }

    private static FontFaceRecord Face(long id, string technology) => new()
    {
        Id = id,
        FaceIndex = 0,
        StrictFingerprint = new byte[] { 1 },
        FamilyName = "Example",
        StyleName = "Regular",
        Names = ["Example"],
        Technology = technology
    };

    private static FontFileRecord File(long id, long generationId, string path, FontFileHealth health, params FontFaceRecord[] faces) => new()
    {
        Id = id,
        GenerationId = generationId,
        Path = path,
        NormalizedPath = path.ToUpperInvariant(),
        PhysicalIdentity = "file-" + id,
        Sha256 = new byte[] { 1 },
        Health = health,
        Error = health == FontFileHealth.Unclassifiable ? "parse failed" : null,
        Faces = faces
    };

    private static DuplicateGroupMemberRecord Member(long fileId, long faceId, DuplicateGroupMemberDecision decision) => new()
    {
        FileId = fileId,
        FaceId = faceId,
        Decision = decision,
        ScoreJson = "{}",
        Reason = decision.ToString()
    };
}
