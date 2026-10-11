using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;
using Jellyfin.Plugin.AssSubsetter.Services;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Persistence;

public sealed partial class SqliteFontRepositoryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishAnalysisAsync_InvalidFaceScoreOrPublicationFailure_ShouldRollbackBatchAndGeneration(bool failPublication)
    {
        await using SqliteRepositoryFixture fixture = await SqliteRepositoryFixture.CreateAsync();
        long oldGeneration = await fixture.Repository.BeginGenerationAsync(FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await fixture.Repository.WriteInventoryAsync(oldGeneration, [CreateFile("old.ttf", "Old")], CancellationToken.None);
        AnalysisBatch oldBatch = await fixture.Repository.PublishAnalysisAsync(new AnalysisBatch
        {
            GenerationId = oldGeneration, Status = AnalysisBatchStatus.Complete, SummaryJson = "{\"faceScores\":[]}"
        }, CancellationToken.None);
        long generation = await fixture.Repository.BeginGenerationAsync(FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await fixture.Repository.WriteInventoryAsync(generation, [CreateFile("new.ttf", "New")], CancellationToken.None);
        FontFileRecord staged = Assert.Single(await fixture.Repository.LoadInventoryAsync(generation, CancellationToken.None));
        FontFaceRecord face = Assert.Single(staged.Faces);
        var valid = new FontFaceAnalysisRecord { FaceId = face.Id, FileId = staged.Id, RetentionScore = 200, IsRuntimePreferred = true, ScoreJson = "{}", VerificationJson = "{}" };
        FontFaceAnalysisRecord[] scores = failPublication ? [valid] : [valid, valid with { FaceId = long.MaxValue }];
        if (failPublication)
        {
            await using SqliteConnection connection = await fixture.Database.OpenConnectionAsync(CancellationToken.None);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER refuse_publication BEFORE UPDATE OF published_generation_id ON schema_info BEGIN SELECT RAISE(ABORT, 'controlled publication failure'); END;";
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var batch = new AnalysisBatch
        {
            GenerationId = generation, Status = AnalysisBatchStatus.Complete, FaceScores = scores,
            SummaryJson = System.Text.Json.JsonSerializer.Serialize(new { faceScores = scores }, System.Text.Json.JsonSerializerOptions.Web)
        };
        if (failPublication)
        {
            await Assert.ThrowsAsync<SqliteException>(() => fixture.Repository.PublishAnalysisAsync(batch, CancellationToken.None));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Repository.PublishAnalysisAsync(batch, CancellationToken.None));
        }

        Assert.Equal(oldGeneration, (await fixture.Repository.GetDatabaseHealthAsync(CancellationToken.None)).PublishedGenerationId);
        Assert.Equal(oldBatch.Id, (await fixture.Repository.GetLatestCompleteAnalysisAsync(CancellationToken.None))!.Id);
        Assert.Single((await fixture.Repository.LoadAnalysisBatchPageAsync(0, 10, CancellationToken.None)).Items);
        FontFaceRecord after = Assert.Single(Assert.Single(await fixture.Repository.LoadInventoryAsync(generation, CancellationToken.None)).Faces);
        Assert.Equal(face.RetentionScore, after.RetentionScore);
        Assert.Equal(face.IsRuntimePreferred, after.IsRuntimePreferred);
        await using SqliteConnection verify = await fixture.Database.OpenConnectionAsync(CancellationToken.None);
        Assert.Equal(1L, await ExecuteScalarAsync<long>(verify, "SELECT COUNT(*) FROM analysis_batch;"));
        Assert.Equal(0L, await ExecuteScalarAsync<long>(verify, $"SELECT status FROM scan_generation WHERE id = {generation};"));
    }

    [Fact]
    public async Task ScanOutcome_ShouldRoundTripSeparatelyFromErrors()
    {
        await using SqliteRepositoryFixture fixture = await SqliteRepositoryFixture.CreateAsync();
        var store = new FontOperationRequestStore(fixture.Repository, TimeProvider.System);
        OperationRequest request = await store.CreateTaskAsync(FontOperationKind.Analyze, "scan-outcome", CancellationToken.None);
        var outcome = new Jellyfin.Plugin.AssSubsetter.Fonts.Api.FontScanOutcomeDto(2, 3, false, []);
        await fixture.Repository.UpdateOperationAsync(request with
        {
            Status = OperationRequestStatus.Complete, CompletedUtc = DateTimeOffset.UtcNow,
            ResultJson = System.Text.Json.JsonSerializer.Serialize(outcome, System.Text.Json.JsonSerializerOptions.Web)
        }, CancellationToken.None);
        OperationRequest loaded = (await fixture.Repository.GetLatestCompletedAnalysisOperationAsync(CancellationToken.None))!;
        Assert.Null(loaded.Error);
        Assert.Equal(request.Id, loaded.Id);
        var actual = System.Text.Json.JsonSerializer.Deserialize<Jellyfin.Plugin.AssSubsetter.Fonts.Api.FontScanOutcomeDto>(loaded.ResultJson!, System.Text.Json.JsonSerializerOptions.Web)!;
        Assert.Equal(outcome.GenerationId, actual.GenerationId);
        Assert.Equal(outcome.AnalysisBatchId, actual.AnalysisBatchId);
        Assert.False(actual.Changed);
        Assert.Empty(actual.ChangeReasons);
    }

    [Fact]
    public async Task UnchangedPhysicalScan_ShouldReuseSqliteGenerationAndStableIdentifiers()
    {
        await using SqliteRepositoryFixture fixture = await SqliteRepositoryFixture.CreateAsync();
        string root = Path.Join(fixture.RootPath, "fonts");
        Directory.CreateDirectory(root);
        using SkiaSharp.SKStreamAsset stream = SkiaSharp.SKTypeface.Default.OpenStream();
        using SkiaSharp.SKData data = SkiaSharp.SKData.Create(stream);
        await File.WriteAllBytesAsync(Path.Join(root, "stable.ttf"), data.ToArray(), TestContext.Current.CancellationToken);
        using var scanner = new FontInventoryScanner(fixture.Repository,
            () => new FontDirectoryPolicyResolver(root, null, null, false),
            new PathPhysicalFileIdentityProvider(), Microsoft.Extensions.Logging.Abstractions.NullLogger<FontInventoryScanner>.Instance);
        FontInventoryScanResult first = await scanner.StageAsync(cancellationToken: TestContext.Current.CancellationToken);
        await fixture.Repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = first.GenerationId, Status = AnalysisBatchStatus.Complete, SummaryJson = "{\"faceScores\":[]}" }, CancellationToken.None);

        FontFileRecord before = Assert.Single(await fixture.Repository.LoadPublishedInventoryAsync(CancellationToken.None));
        FontInventoryScanResult second = await scanner.StageAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(second.HasChanges);
        Assert.Equal(first.GenerationId, second.GenerationId);
        Assert.Equal(first.Input, await fixture.Repository.GetPublishedAnalysisInputAsync(CancellationToken.None));
        FontFileRecord after = Assert.Single(await fixture.Repository.LoadPublishedInventoryAsync(CancellationToken.None));
        Assert.True(before.Id > 0);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(Assert.Single(before.Faces).Id, Assert.Single(after.Faces).Id);
    }

    [Fact]
    public async Task PendingKindQueryReturnsDistinctKindsAndIgnoresClaimedOrCompletedRequests()
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        Assert.Empty(await fixture.Repository.LoadPendingOperationKindsAsync(CancellationToken.None));
        var store = new FontOperationRequestStore(fixture.Repository, TimeProvider.System);
        await store.CreateTaskAsync(FontOperationKind.Analyze, "analysis", CancellationToken.None);
        await store.CreateTaskAsync(FontOperationKind.Analyze, "second-analysis", CancellationToken.None);
        OperationRequest purge = await store.CreateTaskAsync(FontOperationKind.Purge, "purge", CancellationToken.None);
        await fixture.Repository.UpdateOperationAsync(purge with { Status = OperationRequestStatus.Complete }, CancellationToken.None);
        await fixture.Repository.CreateOperationRequestAsync(new OperationRequest { Kind = FontOperationKind.NormalizeNames, IdempotencyKey = "rename", RequestJson = RenameJson(fixture.RootPath) }, CancellationToken.None);
        Assert.Equal([FontOperationKind.Analyze, FontOperationKind.NormalizeNames], await fixture.Repository.LoadPendingOperationKindsAsync(CancellationToken.None));
        await fixture.Repository.ClaimNextOperationAsync(FontOperationKind.NormalizeNames, CancellationToken.None);
        Assert.Equal([FontOperationKind.Analyze], await fixture.Repository.LoadPendingOperationKindsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task LoadInventoryAsync_ShouldReadTheSelectedHistoricalGenerationInsteadOfThePublishedOne()
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        long oldGeneration = await fixture.Repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await fixture.Repository.WriteInventoryAsync(oldGeneration, [CreateFile("old.ttf", "Old Family")], CancellationToken.None);
        await fixture.Repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = oldGeneration, Status = AnalysisBatchStatus.Complete, SummaryJson = "{\"faceScores\":[]}" }, CancellationToken.None);
        long newGeneration = await fixture.Repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await fixture.Repository.WriteInventoryAsync(newGeneration, [CreateFile("new.ttf", "New Family")], CancellationToken.None);
        await fixture.Repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = newGeneration, Status = AnalysisBatchStatus.Complete, SummaryJson = "{\"faceScores\":[]}" }, CancellationToken.None);

        FontFileRecord historical = Assert.Single(await fixture.Repository.LoadInventoryAsync(oldGeneration, CancellationToken.None));

        Assert.Equal("old.ttf", historical.Path);
        Assert.Equal("Old Family", Assert.Single(historical.Faces).FamilyName);
        Assert.Equal("new.ttf", Assert.Single(await fixture.Repository.LoadPublishedInventoryAsync(CancellationToken.None)).Path);
    }

    [Fact]
    public async Task LoadInventoryAsync_ShouldReadPublishedSnapshotWhileStagedGenerationIsBeingWritten()
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        long published = await fixture.Repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await fixture.Repository.WriteInventoryAsync(published, [CreateFile("published.ttf", "Published")], CancellationToken.None);
        await fixture.Repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = published, Status = AnalysisBatchStatus.Complete, SummaryJson = "{\"faceScores\":[]}" }, CancellationToken.None);
        long staged = await fixture.Repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await fixture.Repository.WriteInventoryAsync(staged, [CreateFile("staged.ttf", "Staged")], CancellationToken.None);
        await using SqliteConnection writer = await fixture.Database.OpenConnectionAsync(CancellationToken.None);
        await using SqliteTransaction transaction = (SqliteTransaction)await writer.BeginTransactionAsync(CancellationToken.None);
        await using SqliteCommand update = writer.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE font_file SET size = size + 1 WHERE generation_id = $generation;";
        update.Parameters.AddWithValue("$generation", staged);
        Assert.Equal(1, await update.ExecuteNonQueryAsync(CancellationToken.None));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        FontFileRecord visible = Assert.Single(await fixture.Repository.LoadInventoryAsync(published, timeout.Token));

        Assert.Equal("published.ttf", visible.Path);
        await transaction.RollbackAsync(CancellationToken.None);
    }

    [Fact]
    public async Task GetDatabaseHealthAsync_ShouldExposePublishedGenerationEvenWhenItContainsNoFiles()
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        long generationId = await fixture.Repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await fixture.Repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = generationId, Status = AnalysisBatchStatus.Complete, SummaryJson = "{\"faceScores\":[]}" }, CancellationToken.None);
        DateTimeOffset before = DateTimeOffset.UtcNow;

        FontDatabaseHealth health = await fixture.Repository.GetDatabaseHealthAsync(CancellationToken.None);

        Assert.Equal(generationId, health.PublishedGenerationId);
        Assert.InRange(health.CheckedUtc, before, DateTimeOffset.UtcNow);
        Assert.Equal("wal", health.JournalMode);
    }

    [Fact]
    public async Task ReconcileRenameManifests_ShouldImportAllRequestIdsBeforeQueuingRefreshAnalysis()
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        var manifests = new FileRenameManifestStore(Path.Combine(fixture.RootPath, "rename"));
        for (long id = 1; id <= 2; id++)
        {
            var request = new OperationRequest { Id = id, Kind = FontOperationKind.NormalizeNames, IdempotencyKey = "historical-rename-" + id, RequestJson = RenameJson(fixture.RootPath, fileId: id) };
            var batch = new MaintenanceBatch { Id = id, OperationRequestId = id, Kind = request.Kind, Status = MaintenanceBatchStatus.Complete, SummaryJson = "{}", RenameItems = FontOperationRequestPayloadCodec.ParseRenameExecution(request.RequestJson).Items.Select(item => item with { Status = RenameItemStatus.Renamed, TemporaryPath = Path.Combine(fixture.RootPath, ".ass-subsetter-rename-" + id + "-" + item.FileId + ".tmp") }).ToArray() };
            await manifests.SaveAsync(new RenameManifest(request, batch, id == 1) { RuleFingerprint = new FontNamingRules(FontNamingRules.DefaultFileNameTemplate, "", false).GetFingerprint() }, CancellationToken.None);
        }

        var quarantineManifests = new FileQuarantineManifestStore(Path.Combine(fixture.RootPath, "quarantine"));
        await quarantineManifests.SaveAsync(new QuarantineManifest { Version = 2, EvidenceVersion = Jellyfin.Plugin.AssSubsetter.Fonts.Analysis.FontDuplicateAnalyzer.CurrentEvidenceVersion, BatchId = 3, OperationRequestId = 3, AnalysisBatchId = 1, OperationIdempotencyKey = "historical-quarantine", OperationRequestJson = "{\"batchId\":1,\"selectedFileIds\":[]}", Items = [] }, CancellationToken.None);
        var quarantine = new FontQuarantineManager(fixture.Repository, new FontMaintenanceCoordinator(), new PhysicalQuarantineFileSystem(), quarantineManifests, Path.Combine(fixture.RootPath, "quarantine"), TimeProvider.System, () => new FontDirectoryPolicyResolver(fixture.RootPath, null, null, false));
        using var normalizer = new FontFilenameNormalizer(fixture.Repository, new FontMaintenanceCoordinator(), new PhysicalQuarantineFileSystem(), manifests, () => new FontDirectoryPolicyResolver(fixture.RootPath, null, null, false), new PlatformPhysicalFileIdentityProvider(), new FontRenameCandidateService(fixture.Repository, new FontRenamePlanner(new PhysicalFontRenameDirectoryOperations()), () => new(FontNamingRules.DefaultFileNameTemplate, "", false), () => new FontDirectoryPolicyResolver(fixture.RootPath, null, null, false), TimeProvider.System, new PhysicalQuarantineFileSystem(), new PhysicalFontRenameDirectoryOperations(), new FontMaintenanceCoordinator()), new PhysicalFontRenameDirectoryOperations(), async token => await quarantine.ReconcileAsync(token));
        await normalizer.ReconcileAsync(CancellationToken.None);
        Assert.Equal("historical-rename-2", (await fixture.Repository.GetOperationRequestAsync(2, CancellationToken.None))!.IdempotencyKey);
        Assert.Equal(FontOperationKind.Quarantine, (await fixture.Repository.GetOperationRequestAsync(3, CancellationToken.None))!.Kind);
        OperationRequest analysis = (await fixture.Repository.GetOperationRequestByIdempotencyKeyAsync("rename-refresh:1", CancellationToken.None))!;
        Assert.True(analysis.Id > 3);
        await normalizer.ReconcileAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RenameRefresh_ShouldDeferChangedPathsUntilCompletedAnalysisPublication()
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        string firstRoot = Path.Combine(fixture.RootPath, "first");
        string otherRoot = Path.Combine(fixture.RootPath, "other");
        Directory.CreateDirectory(firstRoot);
        Directory.CreateDirectory(otherRoot);
        using SKStreamAsset stream = SKTypeface.Default.OpenStream();
        using SKData data = SKData.Create(stream);
        await File.WriteAllBytesAsync(Path.Combine(firstRoot, "original.ttf"), data.ToArray(), TestContext.Current.CancellationToken);
        string untouched = Path.Combine(otherRoot, "untouched.ttf");
        await File.WriteAllBytesAsync(untouched, data.ToArray(), TestContext.Current.CancellationToken);
        Func<FontDirectoryPolicyResolver> policy = () => new FontDirectoryPolicyResolver(firstRoot + ";" + otherRoot, null, null, false);
        var identity = new PlatformPhysicalFileIdentityProvider();
        var coordinator = new FontMaintenanceCoordinator();
        using var scanner = new FontInventoryScanner(fixture.Repository, policy, identity, NullLogger<FontInventoryScanner>.Instance);
        using var cache = new FontCacheManager(fixture.Repository, scanner, coordinator);
        long generation = (await cache.StageInventoryAsync(cancellationToken: TestContext.Current.CancellationToken)).GenerationId;
        AnalysisBatch analysis = new AnalysisBatch { GenerationId = generation, Status = AnalysisBatchStatus.Complete, EvidenceVersion = Jellyfin.Plugin.AssSubsetter.Fonts.Analysis.FontDuplicateAnalyzer.CurrentEvidenceVersion, SummaryJson = "{\"faceScores\":[]}" };
        analysis = await cache.PublishAnalyzedGenerationAsync(analysis with { Id = 0 }, CancellationToken.None);
        using var normalizer = new FontFilenameNormalizer(fixture.Repository, coordinator, new PhysicalQuarantineFileSystem(), new FileRenameManifestStore(Path.Combine(fixture.RootPath, "rename")), policy, identity, new FontRenameCandidateService(fixture.Repository, new FontRenamePlanner(new PhysicalFontRenameDirectoryOperations()), () => new(FontNamingRules.DefaultFileNameTemplate, "", false), policy, TimeProvider.System, new PhysicalQuarantineFileSystem(), new PhysicalFontRenameDirectoryOperations(), coordinator), new PhysicalFontRenameDirectoryOperations());
        var candidates = new FontRenameCandidateService(fixture.Repository, new FontRenamePlanner(new PhysicalFontRenameDirectoryOperations()), () => new(FontNamingRules.DefaultFileNameTemplate, "", false), policy, TimeProvider.System, new PhysicalQuarantineFileSystem(), new PhysicalFontRenameDirectoryOperations(), coordinator);
        var completedAnalysis = (await fixture.Repository.GetLatestCompleteAnalysisAsync(CancellationToken.None))!;
        var prepared = await candidates.PrepareForPublicationAsync(completedAnalysis with { Id = 0 }, CancellationToken.None);
        await fixture.Repository.SaveRenameCandidatesAsync(prepared with { State = prepared.State with { AnalysisBatchId = completedAnalysis.Id } }, candidates.ReadRequirements(), null, CancellationToken.None);
        FontRenameSnapshot plan = await candidates.GetCurrentAsync(CancellationToken.None);
        FontRenameCandidate selected = Assert.Single(plan.Items, item => Path.GetDirectoryName(item.File.Path) == firstRoot);
        OperationRequest request = await new FontRenameCandidateService(fixture.Repository, new FontRenamePlanner(new PhysicalFontRenameDirectoryOperations()), () => new(FontNamingRules.DefaultFileNameTemplate, "", false), policy, TimeProvider.System, new PhysicalQuarantineFileSystem(), new PhysicalFontRenameDirectoryOperations(), coordinator).CreateRequestAsync(plan.GenerationId, plan.AnalysisBatchId, plan.RuleFingerprint, [selected.File.Id], "refresh-test", CancellationToken.None);
        MaintenanceBatch rename = await normalizer.ExecuteAsync(request, CancellationToken.None);
        IReadOnlyList<FontFileRecord> files = await fixture.Repository.LoadPublishedInventoryAsync(CancellationToken.None);
        Assert.Equal(2, files.Count);
        Assert.Contains(files, file => file.Path == untouched);
        Assert.Contains(files, file => file.Path == selected.File.Path);
        Assert.All(files, file => Assert.Equal(generation, file.GenerationId));
        Assert.Equal(AnalysisBatchStatus.Stale, (await fixture.Repository.GetAnalysisBatchAsync(analysis.Id, CancellationToken.None))!.Status);
        long nextGeneration = (await cache.StageInventoryAsync(cancellationToken: CancellationToken.None)).GenerationId;
        AnalysisBatch newerAnalysis = new AnalysisBatch { GenerationId = nextGeneration, Status = AnalysisBatchStatus.Complete, SummaryJson = "{\"faceScores\":[]}" };
        newerAnalysis = await cache.PublishAnalyzedGenerationAsync(newerAnalysis with { Id = 0 }, CancellationToken.None);
        files = await fixture.Repository.LoadPublishedInventoryAsync(CancellationToken.None);
        Assert.Contains(files, file => file.Path == untouched);
        Assert.Contains(files, file => file.Path == selected.NewPath);
        Assert.Equal(2, (await fixture.Repository.LoadRecentPerformanceSamplesAsync(1, CancellationToken.None))[0].ReusedCount);
        Assert.Equal(selected.NewPath, (await Jellyfin.Plugin.AssSubsetter.Tests.Services.FontCacheManagerFixture.LookupAsync(cache, new Jellyfin.Plugin.AssSubsetter.Models.FontDescriptor(selected.File.Faces[0].FamilyName, null, false, false)))?.Path);
        OperationRequest rollback = await new FontOperationRequestStore(fixture.Repository, TimeProvider.System).CreateAsync(FontOperationKind.RollbackNames, rename.Id, [selected.File.Id], "rollback-refresh", CancellationToken.None);
        await normalizer.RollbackAsync(rollback, CancellationToken.None);
        Assert.Equal(AnalysisBatchStatus.Stale, (await fixture.Repository.GetAnalysisBatchAsync(newerAnalysis.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task ImportMaintenanceBatchAsync_ShouldRecoverRenameAuditWithoutDerivedInventory()
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        string json = RenameJson(fixture.RootPath, analysisId: 8, fileId: 37);
        var item = FontOperationRequestPayloadCodec.ParseRenameExecution(json).Items.Single();
        var batch = new MaintenanceBatch
        {
            Id = 52, OperationRequestId = 76, Kind = FontOperationKind.NormalizeNames, Status = MaintenanceBatchStatus.Complete,
            StartedUtc = DateTimeOffset.UnixEpoch, SummaryJson = "{}",
            RenameItems = [item with { TemporaryPath = Path.Combine(fixture.RootPath, ".ass-subsetter-rename-52-37.tmp"), Status = RenameItemStatus.Renamed }]
        };
        await fixture.Repository.ImportMaintenanceBatchAsync(batch, 8, "rename-audit", json, CancellationToken.None);
        await fixture.Repository.ImportMaintenanceBatchAsync(batch, 8, "rename-audit", json, CancellationToken.None);
        MaintenanceBatch loaded = (await fixture.Repository.GetMaintenanceBatchAsync(52, CancellationToken.None))!;
        Assert.Equal(item.NewPath, Assert.Single(loaded.RenameItems).NewPath);
        Assert.Equal(RenameItemStatus.Renamed, Assert.Single(loaded.RenameItems).Status);
        Assert.Equal(json, (await fixture.Repository.GetOperationRequestAsync(76, CancellationToken.None))!.RequestJson);
        Assert.Empty(await fixture.Repository.LoadPublishedInventoryAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TypedNamesAndRenamePlan_ShouldSurviveRepositoryReloadWithoutCreatingOperation()
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        long generation = await fixture.Repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        FontFileRecord file = CreateFile("old.ttf", "Family");
        OpenTypeNameRecord[] names = [new(3, 1, 1033, 6, "ReliablePostScript")];
        file = file with { Faces = [file.Faces[0] with { NameRecords = names }] };
        await fixture.Repository.WriteInventoryAsync(generation, [file], CancellationToken.None);
        await fixture.Repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = generation, Status = AnalysisBatchStatus.Complete, SummaryJson = "{\"faceScores\":[]}" }, CancellationToken.None);
        FontFileRecord loaded = Assert.Single(await fixture.Repository.LoadPublishedInventoryAsync(CancellationToken.None));
        Assert.Equal(names, loaded.Faces[0].NameRecords);
        RuntimeFontFace runtime = Assert.Single(await fixture.Repository.LoadPublishedRuntimeFacesAsync(CancellationToken.None));
        Assert.Equal(names, runtime.NameRecords);

    }

    [Fact]
    public async Task UnicodeCoverage_ShouldSurviveInventoryReuseAndRuntimePublication()
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        long first = await fixture.Repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        FontFileRecord file = CreateFile("coverage.ttf", "Coverage Family");
        file = file with { Faces = [file.Faces[0] with { UnicodeCoverage = [new UnicodeRange(0x41, 0x5A), new UnicodeRange(0x4E00, 0x4E01)] }] };
        await fixture.Repository.WriteInventoryAsync(first, [file], CancellationToken.None);
        await fixture.Repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = first, Status = AnalysisBatchStatus.Complete, SummaryJson = "{\"faceScores\":[]}" }, CancellationToken.None);

        FontFileRecord metadata = Assert.Single(await fixture.Repository.LoadPublishedInventoryAsync(CancellationToken.None));
        Assert.Empty(Assert.Single(metadata.Faces).UnicodeCoverage);
        FontFileRecord loaded = Assert.Single(await fixture.Repository.LoadPublishedInventoryForReuseAsync(CancellationToken.None));
        Assert.Equal([new UnicodeRange(0x41, 0x5A), new UnicodeRange(0x4E00, 0x4E01)], Assert.Single(loaded.Faces).UnicodeCoverage);
        RuntimeFontFace runtime = Assert.Single(await fixture.Repository.LoadPublishedRuntimeFacesAsync(CancellationToken.None));
        Assert.Equal([new UnicodeRange(0x41, 0x5A), new UnicodeRange(0x4E00, 0x4E01)], runtime.UnicodeCoverage);
    }

    [Fact]
    public async Task PublishAnalysisAsync_ShouldSwitchVisibleInventoryAtomically()
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        long oldGeneration = await fixture.Repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await fixture.Repository.WriteInventoryAsync(oldGeneration, [CreateFile("old.ttf", "Old Family")], CancellationToken.None);
        await fixture.Repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = oldGeneration, Status = AnalysisBatchStatus.Complete, SummaryJson = "{\"faceScores\":[]}" }, CancellationToken.None);

        long newGeneration = await fixture.Repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await fixture.Repository.WriteInventoryAsync(
            newGeneration,
            [CreateFile("new.ttf", "New Family") with { PathAliases = ["new-alias.ttf"] }],
            CancellationToken.None);
        Assert.Equal(["old.ttf"], (await fixture.Repository.LoadPublishedRuntimeFacesAsync(CancellationToken.None)).Select(face => face.Path));

        await fixture.Repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = newGeneration, Status = AnalysisBatchStatus.Complete, SummaryJson = "{\"faceScores\":[]}" }, CancellationToken.None);
        Assert.Equal(["new.ttf"], (await fixture.Repository.LoadPublishedRuntimeFacesAsync(CancellationToken.None)).Select(face => face.Path));
        FontFileRecord published = Assert.Single(await fixture.Repository.LoadPublishedInventoryAsync(CancellationToken.None));
        Assert.Equal("new.ttf", published.Path);
        Assert.Equal(["new-alias.ttf"], published.PathAliases);
        Assert.Equal("New Family", Assert.Single(published.Faces).FamilyName);
    }

    [Fact]
    public async Task PublishAnalysisAsync_ShouldLeaveCurrentGenerationPublished_WhenTargetDoesNotExist()
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        long currentGeneration = await fixture.Repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await fixture.Repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = currentGeneration, Status = AnalysisBatchStatus.Complete, SummaryJson = "{\"faceScores\":[]}" }, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = long.MaxValue, Status = AnalysisBatchStatus.Complete, SummaryJson = "{\"faceScores\":[]}" }, CancellationToken.None));

        await using SqliteConnection connection = await fixture.Database.OpenConnectionAsync(CancellationToken.None);
        Assert.Equal(currentGeneration, await ExecuteScalarAsync<long>(connection, "SELECT published_generation_id FROM schema_info WHERE singleton_id = 1;"));
        Assert.Equal(1L, await ExecuteScalarAsync<long>(connection, $"SELECT status FROM scan_generation WHERE id = {currentGeneration};"));
    }

    [Fact]
    public async Task InitializeAsync_ShouldConfigureSQLiteAndCreateCoreTables()
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        await using SqliteConnection connection = await fixture.Database.OpenConnectionAsync(CancellationToken.None);

        Assert.Equal("wal", await ExecuteScalarAsync<string>(connection, "PRAGMA journal_mode;"));
        Assert.Equal(1L, await ExecuteScalarAsync<long>(connection, "PRAGMA foreign_keys;"));
        Assert.Equal(SqliteSchema.CurrentVersion, await ExecuteScalarAsync<long>(connection, "PRAGMA user_version;"));

        string[] expectedTables =
        [
            "analysis_batch",
            "duplicate_group",
            "face_coverage",
            "face_name",
            "face_name_evidence",
            "face_table",
            "font_axis",
            "font_deep_evidence",
            "font_face",
            "font_file",
            "font_named_instance",
            "font_path_alias",
            "font_relation",
            "group_member",
            "maintenance_batch",
            "operation_request",
            "performance_sample",
            "quarantine_item",
            "rename_candidate",
            "rename_candidate_state",
            "rename_item",
            "rename_request_item",
            "scan_generation",
            "schema_info"
        ];
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
        var actualTables = new List<string>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            actualTables.Add(reader.GetString(0));
        }

        Assert.Equal(expectedTables, actualTables);
    }

    [Fact]
    public async Task InitializeAsync_ShouldSupportDatabasePathContainingQuote()
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync("quoted'folder");

        long generationId = await fixture.Repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);

        Assert.True(generationId > 0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(9)]
    public async Task EnsureCreatedAsync_ShouldRejectUnsupportedSchemaWithoutChangingStoredData(int version)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA user_version = {version}; CREATE TABLE preserved_data(value TEXT NOT NULL); INSERT INTO preserved_data VALUES ('original');";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => SqliteSchema.EnsureCreatedAsync(connection, TestContext.Current.CancellationToken));

        Assert.Equal(version, await ExecuteScalarAsync<long>(connection, "PRAGMA user_version;"));
        Assert.Equal("original", await ExecuteScalarAsync<string>(connection, "SELECT value FROM preserved_data;"));
        Assert.Equal(1L, await ExecuteScalarAsync<long>(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table';"));
    }

    [Fact]
    public async Task WriteInventoryAsync_ShouldRollbackWholeBatch_WhenNormalizedPathRepeats()
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        long generationId = await fixture.Repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        FontFileRecord first = CreateFile("first.ttf", "First Family");
        FontFileRecord second = CreateFile("FIRST.TTF", "Second Family") with { NormalizedPath = first.NormalizedPath };

        await Assert.ThrowsAsync<SqliteException>(() => fixture.Repository.WriteInventoryAsync(generationId, [first, second], CancellationToken.None));

        await using SqliteConnection connection = await fixture.Database.OpenConnectionAsync(CancellationToken.None);
        Assert.Equal(0L, await ExecuteScalarAsync<long>(connection, "SELECT COUNT(*) FROM font_file;"));
    }

    [Fact]
    public async Task WriteInventoryAsync_ShouldRejectPublishedGeneration()
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        long generationId = await fixture.Repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await fixture.Repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = generationId, Status = AnalysisBatchStatus.Complete, SummaryJson = "{\"faceScores\":[]}" }, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Repository.WriteInventoryAsync(generationId, [CreateFile("late.ttf", "Late Family")], CancellationToken.None));
    }

    [Fact]
    public async Task InitializeAsync_ShouldPreserveCorruptDatabaseAndCreateCleanCatalog()
    {
        string root = Path.Join(Path.GetTempPath(), "ass-subsetter-tests", Guid.NewGuid().ToString("N"));
        var paths = new FontStoragePaths(root, Path.Join(root, "catalog.db"), Path.Join(root, "quarantine"), Path.Join(root, "manifests"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(paths.DatabasePath, "this is not sqlite", TestContext.Current.CancellationToken);
        var database = new FontDatabase(paths);
        var repository = new SqliteFontRepository(database);

        try
        {
            await repository.InitializeAsync(CancellationToken.None);

            Assert.NotNull(database.RecoveredCorruptDatabasePath);
            Assert.True(File.Exists(database.RecoveredCorruptDatabasePath));
            Assert.True(await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None) > 0);
        }
        finally
        {
            await database.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadAnalysisBatchPageAsync_ShouldReturnExactPageBeyondTheFirstRow()
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        long generationId = await fixture.Repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        for (int index = 0; index < 3; index++)
        {
            generationId = await fixture.Repository.BeginGenerationAsync(FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
            await fixture.Repository.PublishAnalysisAsync(new AnalysisBatch
            {
                GenerationId = generationId,
                Status = AnalysisBatchStatus.Complete,
                EvidenceVersion = 1,
                StartedUtc = DateTimeOffset.UnixEpoch.AddMinutes(index),
                CompletedUtc = DateTimeOffset.UnixEpoch.AddMinutes(index),
                SummaryJson = "{\"faceScores\":[]}"
            }, CancellationToken.None);
        }

        FontRepositoryPage<AnalysisBatch> page = await fixture.Repository.LoadAnalysisBatchPageAsync(1, 1, CancellationToken.None);

        Assert.Equal(3, page.TotalCount);
        Assert.Equal(2, Assert.Single(page.Items).Id);
    }

    [Fact]
    public async Task LoadMaintenanceBatchPageAsync_ShouldApplyFilterAndExactCount()
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        for (int index = 0; index < 3; index++)
        {
            OperationRequest operation = await fixture.Repository.CreateOperationRequestAsync(new OperationRequest
            {
                Kind = FontOperationKind.Quarantine,
                Status = OperationRequestStatus.Pending,
                IdempotencyKey = "page-maintenance-" + index,
                RequestJson = "{}",
                CreatedUtc = DateTimeOffset.UnixEpoch.AddMinutes(index)
            }, CancellationToken.None);
            await fixture.Repository.SaveMaintenanceBatchAsync(new MaintenanceBatch
            {
                OperationRequestId = operation.Id,
                Kind = index == 1 ? FontOperationKind.NormalizeNames : FontOperationKind.Quarantine,
                Status = MaintenanceBatchStatus.Complete,
                StartedUtc = DateTimeOffset.UnixEpoch.AddMinutes(index),
                SummaryJson = "{\"faceScores\":[]}"
            }, CancellationToken.None);
        }

        FontRepositoryPage<MaintenanceBatch> page = await fixture.Repository.LoadMaintenanceBatchPageAsync(0, 1, new HashSet<FontOperationKind> { FontOperationKind.Quarantine }, CancellationToken.None);

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(FontOperationKind.Quarantine, Assert.Single(page.Items).Kind);
    }

    [Fact]
    public async Task PublishAnalysisAsync_ShouldReturnLatestCompleteReport()
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        long generationId = await fixture.Repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await fixture.Repository.WriteInventoryAsync(
            generationId,
            [CreateFile("retained.ttf", "Shared Family"), CreateFile("duplicate.ttf", "Shared Family")],
            CancellationToken.None);

        IReadOnlyList<FontFileRecord> inventory = await fixture.Repository.LoadInventoryAsync(generationId, CancellationToken.None);
        FontFileRecord retainedFile = inventory.Single(file => file.Path == "retained.ttf");
        FontFileRecord duplicateFile = inventory.Single(file => file.Path == "duplicate.ttf");
        long retainedFaceId = Assert.Single(retainedFile.Faces).Id;
        long duplicateFaceId = Assert.Single(duplicateFile.Faces).Id;
        var report = new AnalysisBatch
        {
            GenerationId = generationId,
            Status = AnalysisBatchStatus.Complete,
            EvidenceVersion = 1,
            StartedUtc = DateTimeOffset.UnixEpoch,
            CompletedUtc = DateTimeOffset.UnixEpoch.AddMinutes(1),
            CandidateCount = 3,
            ComparisonCount = 2,
            ReclaimableBytes = 128,
            SummaryJson = "{\"complete\":true,\"faceScores\":[]}",
            Groups =
            [
                new DuplicateGroupRecord
                {
                    RetainedFaceId = retainedFaceId,
                    ReclaimableBytes = duplicateFile.Size,
                    EvidenceJson = "{\"kind\":\"IdenticalFileHash\"}",
                    Members =
                    [
                        new DuplicateGroupMemberRecord
                        {
                            FaceId = retainedFaceId,
                            FileId = retainedFile.Id,
                            Decision = DuplicateGroupMemberDecision.Retained,
                            RetentionScore = 100,
                            ScoreJson = "{\"total\":100}",
                            Reason = "retained"
                        },
                        new DuplicateGroupMemberRecord
                        {
                            FaceId = duplicateFaceId,
                            FileId = duplicateFile.Id,
                            Decision = DuplicateGroupMemberDecision.QuarantineCandidate,
                            RetentionScore = 90,
                            ScoreJson = "{\"total\":90}",
                            Reason = "duplicate"
                        }
                    ]
                }
            ]
        };

        AnalysisBatch persisted = await fixture.Repository.PublishAnalysisAsync(report, CancellationToken.None);
        AnalysisBatch? actual = await fixture.Repository.GetLatestCompleteAnalysisAsync(CancellationToken.None);
        IReadOnlyList<AnalysisBatch> loaded = (await fixture.Repository.LoadAnalysisBatchPageAsync(0, 10, CancellationToken.None)).Items;

        Assert.True(persisted.Id > 0);
        Assert.Equal(persisted.Id, Assert.Single(loaded).Id);
        Assert.NotNull(actual);
        Assert.Equal(persisted.Id, actual.Id);
        Assert.Equal(persisted.GenerationId, actual.GenerationId);
        Assert.Equal(persisted.Status, actual.Status);
        Assert.Equal(persisted.SummaryJson, actual.SummaryJson);
        Assert.Empty(actual.Relations);
        DuplicateGroupRecord group = Assert.Single(actual.Groups);
        Assert.Equal(retainedFaceId, group.RetainedFaceId);
        Assert.Equal(duplicateFile.Size, group.ReclaimableBytes);
        Assert.Equal(2, group.Members.Count);
        Assert.Contains(group.Members, member => member.Decision == DuplicateGroupMemberDecision.QuarantineCandidate);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"faceScores\":null}")]
    [InlineData("{\"faceScores\":{}}")]
    [InlineData("{\"faceScores\":[null]}")]
    public async Task GetAnalysisBatchAsync_ShouldRejectCompletedSummaryWithoutCurrentFaceEvidence(string summaryJson)
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        long generation = await fixture.Repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Repository.PublishAnalysisAsync(
            new AnalysisBatch { GenerationId = generation, Status = AnalysisBatchStatus.Complete, SummaryJson = summaryJson }, CancellationToken.None));
        Assert.Empty((await fixture.Repository.LoadAnalysisBatchPageAsync(0, 10, CancellationToken.None)).Items);

    }

    [Theory]
    [InlineData(AnalysisBatchStatus.Running)]
    [InlineData(AnalysisBatchStatus.Failed)]
    public async Task PublishAnalysisAsync_ShouldRejectUnfinishedAnalysis(AnalysisBatchStatus status)
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        long generation = await fixture.Repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Repository.PublishAnalysisAsync(
            new AnalysisBatch { GenerationId = generation, Status = status, SummaryJson = "{}" }, CancellationToken.None));
        Assert.Empty((await fixture.Repository.LoadAnalysisBatchPageAsync(0, 10, CancellationToken.None)).Items);

    }

    [Fact]
    public async Task CreateOperationRequestAsync_ShouldReturnExistingRequest_ForDuplicateIdempotencyKey()
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        var request = new OperationRequest
        {
            Kind = FontOperationKind.Quarantine,
            Status = OperationRequestStatus.Pending,
            IdempotencyKey = "same-request",
            RequestJson = "{}",
            CreatedUtc = DateTimeOffset.UnixEpoch
        };

        OperationRequest first = await fixture.Repository.CreateOperationRequestAsync(request, CancellationToken.None);
        OperationRequest second = await fixture.Repository.CreateOperationRequestAsync(request, CancellationToken.None);
        OperationRequest? loaded = await fixture.Repository.GetOperationRequestAsync(first.Id, CancellationToken.None);
        OperationRequest? claimed = await fixture.Repository.ClaimNextOperationAsync(FontOperationKind.Quarantine, CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first, loaded);
        Assert.Equal(first.Id, claimed?.Id);
        Assert.Equal(OperationRequestStatus.Running, claimed?.Status);
    }

    [Theory]
    [InlineData(FontOperationKind.Quarantine)]
    [InlineData(FontOperationKind.Restore)]
    [InlineData(FontOperationKind.Purge)]
    [InlineData(FontOperationKind.NormalizeNames)]
    public async Task PruneAnalysisBatchesAsync_ShouldKeepNewestTenAndActiveReferences(FontOperationKind operationKind)
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        long generationId = await fixture.Repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);

        DateTimeOffset now = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var reports = new List<AnalysisBatch>();
        for (int index = 0; index < 12; index++)
        {
            generationId = await fixture.Repository.BeginGenerationAsync(FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
            reports.Add(await fixture.Repository.PublishAnalysisAsync(
                new AnalysisBatch
                {
                    GenerationId = generationId,
                    Status = AnalysisBatchStatus.Complete,
                    EvidenceVersion = 1,
                    StartedUtc = now.AddDays(-60 + index),
                    CompletedUtc = now.AddDays(-60 + index).AddMinutes(1),
                    SummaryJson = "{\"faceScores\":[]}"
                },
                CancellationToken.None));
        }

        await fixture.Repository.CreateOperationRequestAsync(
            new OperationRequest
            {
                Kind = operationKind,
                Status = OperationRequestStatus.Pending,
                AnalysisBatchId = reports[0].Id,
                IdempotencyKey = $"active-old-report-{operationKind}",
                RequestJson = operationKind == FontOperationKind.NormalizeNames ? RenameJson(fixture.RootPath, reports[0].GenerationId, reports[0].Id) : "{}",
                CreatedUtc = now
            },
            CancellationToken.None);

        OperationRequest completed = await fixture.Repository.CreateOperationRequestAsync(
            new OperationRequest
            {
                Kind = operationKind,
                Status = OperationRequestStatus.Complete,
                AnalysisBatchId = reports[1].Id,
                IdempotencyKey = $"completed-old-report-{operationKind}",
                RequestJson = operationKind == FontOperationKind.NormalizeNames ? RenameJson(fixture.RootPath, reports[1].GenerationId, reports[1].Id) : "{}",
                CreatedUtc = now
            },
            CancellationToken.None);

        int deleted = await fixture.Repository.PruneAnalysisBatchesAsync(10, CancellationToken.None);

        Assert.Equal(1, deleted);
        Assert.NotNull(await fixture.Repository.GetAnalysisBatchAsync(reports[0].Id, CancellationToken.None));
        Assert.Null(await fixture.Repository.GetAnalysisBatchAsync(reports[1].Id, CancellationToken.None));
        Assert.Null((await fixture.Repository.GetOperationRequestAsync(completed.Id, CancellationToken.None))!.AnalysisBatchId);
    }

    [Fact]
    public async Task PruneAnalysisBatchesAsync_ZeroRetainsAllAndCountIncludesStaleResults()
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        long generationId = await fixture.Repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);

        var reports = new List<AnalysisBatch>();
        foreach (AnalysisBatchStatus status in new[] { AnalysisBatchStatus.Complete, AnalysisBatchStatus.Stale, AnalysisBatchStatus.Complete })
        {
            int index = reports.Count;
            generationId = await fixture.Repository.BeginGenerationAsync(FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
            reports.Add(await fixture.Repository.PublishAnalysisAsync(
                new AnalysisBatch
                {
                    GenerationId = generationId,
                    Status = AnalysisBatchStatus.Complete,
                    EvidenceVersion = 1,
                    StartedUtc = DateTimeOffset.UnixEpoch.AddDays(index),
                    CompletedUtc = DateTimeOffset.UnixEpoch.AddDays(index).AddMinutes(1),
                    SummaryJson = "{\"faceScores\":[]}"
                },
                CancellationToken.None));
            if (status == AnalysisBatchStatus.Stale) await fixture.Repository.MarkAnalysisStaleAsync(generationId, CancellationToken.None);
        }

        Assert.Equal(0, await fixture.Repository.PruneAnalysisBatchesAsync(0, CancellationToken.None));
        Assert.Equal(3, (await fixture.Repository.LoadAnalysisBatchPageAsync(0, 10, CancellationToken.None)).Items.Count);

        Assert.Equal(2, await fixture.Repository.PruneAnalysisBatchesAsync(1, CancellationToken.None));
        Assert.Equal(reports[2].Id, Assert.Single((await fixture.Repository.LoadAnalysisBatchPageAsync(0, 10, CancellationToken.None)).Items).Id);
    }

    [Fact]
    public async Task PruneAnalysisBatchesAsync_ShouldPreserveCurrentValidReportBeyondCount()
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        long oldGeneration = await fixture.Repository.BeginGenerationAsync(FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        AnalysisBatch newerTime = await fixture.Repository.PublishAnalysisAsync(new AnalysisBatch
        {
            GenerationId = oldGeneration, Status = AnalysisBatchStatus.Complete,
            StartedUtc = DateTimeOffset.UnixEpoch.AddDays(1), CompletedUtc = DateTimeOffset.UnixEpoch.AddDays(1),
            SummaryJson = "{\"faceScores\":[]}"
        }, CancellationToken.None);
        long currentGeneration = await fixture.Repository.BeginGenerationAsync(FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        AnalysisBatch valid = await fixture.Repository.PublishAnalysisAsync(new AnalysisBatch
        {
            GenerationId = currentGeneration, Status = AnalysisBatchStatus.Complete,
            StartedUtc = DateTimeOffset.UnixEpoch, CompletedUtc = DateTimeOffset.UnixEpoch,
            SummaryJson = "{\"faceScores\":[]}"
        }, CancellationToken.None);
        Assert.Equal(0, await fixture.Repository.PruneAnalysisBatchesAsync(1, CancellationToken.None));
        Assert.NotNull(await fixture.Repository.GetAnalysisBatchAsync(newerTime.Id, CancellationToken.None));
        Assert.Equal(valid.Id, (await fixture.Repository.GetLatestCompleteAnalysisAsync(CancellationToken.None))!.Id);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    public async Task PruneObsoleteGenerationsAsync_RespectsPublishedGenerationRetention(int retentionCount, int expectedDeleted)
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        var generations = new List<long>();
        for (int index = 0; index < 3; index++)
        {
            long generation = await fixture.Repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
            await fixture.Repository.WriteInventoryAsync(generation, [CreateFile($"generation-{index}.ttf", $"Generation {index}")], CancellationToken.None);
            await fixture.Repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = generation, Status = AnalysisBatchStatus.Complete, SummaryJson = "{\"faceScores\":[]}" }, CancellationToken.None);
            generations.Add(generation);
        }

        await fixture.Repository.PruneAnalysisBatchesAsync(1, CancellationToken.None);
        int deleted = await fixture.Repository.PruneObsoleteGenerationsAsync(
            retentionCount,
            DateTimeOffset.UtcNow.AddDays(-1),
            null,
            CancellationToken.None);

        Assert.Equal(expectedDeleted, deleted);
        for (int index = 0; index < generations.Count; index++)
        {
            IReadOnlyList<FontFileRecord> files = await fixture.Repository.LoadInventoryAsync(generations[index], CancellationToken.None);
            Assert.Equal(index < expectedDeleted ? 0 : 1, files.Count);
        }
    }

    [Fact]
    public async Task PruneObsoleteGenerationsAsync_RemovesOnlyUnreferencedInventory()
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        long obsolete = await AddGenerationAsync("obsolete.ttf");
        await fixture.Repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = obsolete, Status = AnalysisBatchStatus.Complete, SummaryJson = "{\"faceScores\":[]}" }, CancellationToken.None);
        long reported = await AddGenerationAsync("reported.ttf");

        await fixture.Repository.PublishAnalysisAsync(new AnalysisBatch
        {
            GenerationId = reported, Status = AnalysisBatchStatus.Complete, SummaryJson = "{\"faceScores\":[]}"
        }, CancellationToken.None);
        long planned = await AddGenerationAsync("planned.ttf");

        long published = await AddGenerationAsync("published.ttf");
        await fixture.Repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = published, Status = AnalysisBatchStatus.Complete, SummaryJson = "{\"faceScores\":[]}" }, CancellationToken.None);
        long abandoned = await AddGenerationAsync("abandoned.ttf");
        long recent = await AddGenerationAsync("recent.ttf");
        await using (SqliteConnection connection = await fixture.Database.OpenConnectionAsync(CancellationToken.None))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "UPDATE scan_generation SET created_utc = $old WHERE id = $abandoned;";
            command.Parameters.AddWithValue("$old", DateTimeOffset.UtcNow.AddDays(-2).ToString("O"));
            command.Parameters.AddWithValue("$abandoned", abandoned);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await fixture.Repository.PruneAnalysisBatchesAsync(2, CancellationToken.None);
        int deleted = await fixture.Repository.PruneObsoleteGenerationsAsync(1, DateTimeOffset.UtcNow.AddDays(-1), null, CancellationToken.None);

        Assert.Equal(2, deleted);
        foreach (long generation in new[] { obsolete, abandoned })
        {
            Assert.Empty(await fixture.Repository.LoadInventoryAsync(generation, CancellationToken.None));
        }

        foreach (long generation in new[] { reported, planned, published, recent })
        {
            Assert.Single(await fixture.Repository.LoadInventoryAsync(generation, CancellationToken.None));
        }

        await using SqliteConnection verify = await fixture.Database.OpenConnectionAsync(CancellationToken.None);
        Assert.Equal(0L, await ExecuteScalarAsync<long>(verify, "SELECT COUNT(*) FROM pragma_foreign_key_check;"));

        async Task<long> AddGenerationAsync(string path)
        {
            long generation = await fixture.Repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
            await fixture.Repository.WriteInventoryAsync(generation, [CreateFile(path, path)], CancellationToken.None);
            return generation;
        }
    }

    [Fact]
    public async Task PruneAnalysisBatchesAsync_ReleasesTheirSupersededInventory()
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        long oldGeneration = await fixture.Repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await fixture.Repository.WriteInventoryAsync(oldGeneration, [CreateFile("old.ttf", "Old")], CancellationToken.None);

        AnalysisBatch oldReport = await fixture.Repository.PublishAnalysisAsync(new AnalysisBatch
        {
            GenerationId = oldGeneration,
            Status = AnalysisBatchStatus.Complete,
            StartedUtc = DateTimeOffset.UnixEpoch,
            CompletedUtc = DateTimeOffset.UnixEpoch,
            SummaryJson = "{\"faceScores\":[]}"
        }, CancellationToken.None);
        long currentGeneration = await fixture.Repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await fixture.Repository.WriteInventoryAsync(currentGeneration, [CreateFile("current.ttf", "Current")], CancellationToken.None);

        await fixture.Repository.PublishAnalysisAsync(new AnalysisBatch
        {
            GenerationId = currentGeneration,
            Status = AnalysisBatchStatus.Complete,
            StartedUtc = DateTimeOffset.UnixEpoch.AddDays(1),
            CompletedUtc = DateTimeOffset.UnixEpoch.AddDays(1),
            SummaryJson = "{\"faceScores\":[]}"
        }, CancellationToken.None);

        Assert.Equal(1, await fixture.Repository.PruneAnalysisBatchesAsync(1, CancellationToken.None));
        Assert.Null(await fixture.Repository.GetAnalysisBatchAsync(oldReport.Id, CancellationToken.None));
        Assert.Equal(1, await fixture.Repository.PruneObsoleteGenerationsAsync(1, DateTimeOffset.UtcNow.AddDays(-1), null, CancellationToken.None));
        Assert.Empty(await fixture.Repository.LoadInventoryAsync(oldGeneration, CancellationToken.None));
        Assert.Single(await fixture.Repository.LoadPublishedInventoryAsync(CancellationToken.None));
    }

    [Fact]
    public async Task LoadQuarantineItemPageAsync_ShowsLatestActiveStateAcrossBatches()
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        await AddItemAsync("old.ttf", QuarantineItemStatus.Quarantined, FontOperationKind.Quarantine);
        await AddItemAsync("failed.ttf", QuarantineItemStatus.FailedIo, FontOperationKind.Quarantine);
        await AddItemAsync("old.ttf", QuarantineItemStatus.Restored, FontOperationKind.Quarantine);
        await AddItemAsync("pending.ttf", QuarantineItemStatus.Pending, FontOperationKind.Quarantine);
        long currentBatchId = await AddItemAsync("current.ttf", QuarantineItemStatus.Quarantined, FontOperationKind.Quarantine);
        await AddItemAsync("other-kind.ttf", QuarantineItemStatus.Quarantined, FontOperationKind.Restore);

        FontRepositoryPage<QuarantineItemRecord> quarantined = await fixture.Repository.LoadQuarantineItemPageAsync(
            0, 1, new HashSet<QuarantineItemStatus> { QuarantineItemStatus.Quarantined }, CancellationToken.None);
        FontRepositoryPage<QuarantineItemRecord> failed = await fixture.Repository.LoadQuarantineItemPageAsync(
            0, 1, new HashSet<QuarantineItemStatus> { QuarantineItemStatus.FailedIo }, CancellationToken.None);
        FontRepositoryPage<QuarantineItemRecord> pending = await fixture.Repository.LoadQuarantineItemPageAsync(
            0, 1, new HashSet<QuarantineItemStatus> { QuarantineItemStatus.Pending }, CancellationToken.None);

        Assert.Equal(1, quarantined.TotalCount);
        Assert.Equal("current.ttf", Assert.Single(quarantined.Items).SourcePath);
        Assert.Equal(currentBatchId, quarantined.Items[0].MaintenanceBatchId);
        Assert.Equal("failed.ttf", Assert.Single(failed.Items).SourcePath);
        Assert.Equal("pending.ttf", Assert.Single(pending.Items).SourcePath);

        async Task<long> AddItemAsync(string path, QuarantineItemStatus status, FontOperationKind kind)
        {
            OperationRequest request = await fixture.Repository.CreateOperationRequestAsync(new OperationRequest
            {
                Kind = kind,
                Status = OperationRequestStatus.Complete,
                IdempotencyKey = $"quarantine-view-{path}-{status}-{kind}",
                RequestJson = "{}",
                CreatedUtc = DateTimeOffset.UnixEpoch
            }, CancellationToken.None);
            MaintenanceBatch batch = await fixture.Repository.SaveMaintenanceBatchAsync(new MaintenanceBatch
            {
                OperationRequestId = request.Id,
                Kind = kind,
                Status = MaintenanceBatchStatus.Complete,
                StartedUtc = DateTimeOffset.UnixEpoch,
                SummaryJson = "{\"faceScores\":[]}"
            }, CancellationToken.None);
            await fixture.Repository.UpdateMaintenanceBatchAsync(batch with
            {
                Items = [new QuarantineItemRecord
                {
                    FileId = request.Id,
                    SourcePath = path,
                    StagingPath = "staging/" + path,
                    QuarantinePath = "quarantine/" + path,
                    Sha256 = new byte[] { 1 },
                    Status = status
                }]
            }, CancellationToken.None);
            return batch.Id;
        }
    }

    [Fact]
    public async Task MaintenanceStateAndDatabaseHealth_ShouldRoundTripThroughRepository()
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        OperationRequest request = await fixture.Repository.CreateOperationRequestAsync(
            new OperationRequest
            {
                Kind = FontOperationKind.NormalizeNames,
                Status = OperationRequestStatus.Pending,
                IdempotencyKey = "rename-request",
                RequestJson = RenameJson(fixture.RootPath),
                CreatedUtc = DateTimeOffset.UnixEpoch
            },
            CancellationToken.None);
        var batch = new MaintenanceBatch
        {
            OperationRequestId = request.Id,
            Kind = request.Kind,
            Status = MaintenanceBatchStatus.Running,
            StartedUtc = DateTimeOffset.UnixEpoch,
            SummaryJson = "{\"faceScores\":[]}"
        };

        MaintenanceBatch persisted = await fixture.Repository.SaveMaintenanceBatchAsync(batch, CancellationToken.None);
        MaintenanceBatch? actual = await fixture.Repository.GetMaintenanceBatchAsync(persisted.Id, CancellationToken.None);
        IReadOnlyList<MaintenanceBatch> loaded = (await fixture.Repository.LoadMaintenanceBatchPageAsync(0, 10, null, CancellationToken.None)).Items;
        FontDatabaseHealth health = await fixture.Repository.GetDatabaseHealthAsync(CancellationToken.None);

        Assert.Equal(persisted, actual);
        Assert.Equal(persisted.Id, Assert.Single(loaded).Id);
        Assert.Equal(SqliteSchema.CurrentVersion, health.SchemaVersion);
        Assert.Equal("wal", health.JournalMode);
        Assert.EndsWith("catalog.db", health.DatabasePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportMaintenanceBatchAsync_ShouldRestoreManifestItemsWithoutDerivedInventory()
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        var imported = new MaintenanceBatch
        {
            Id = 77,
            OperationRequestId = 88,
            Kind = FontOperationKind.Quarantine,
            Status = MaintenanceBatchStatus.Partial,
            StartedUtc = DateTimeOffset.UnixEpoch,
            SummaryJson = "{\"imported\":true}",
            Items =
            [
                new QuarantineItemRecord
                {
                    MaintenanceBatchId = 77,
                    FileId = 999,
                    SourcePath = "original.ttf",
                    StagingPath = "00077/items/00999.stage",
                    QuarantinePath = "00077/items/00999.font",
                    Sha256 = new byte[32],
                    Size = 123,
                    Status = QuarantineItemStatus.Quarantined,
                    QuarantinedUtc = DateTimeOffset.UnixEpoch,
                    ExpiresUtc = DateTimeOffset.UnixEpoch.AddDays(30)
                }
            ]
        };

        await fixture.Repository.ImportMaintenanceBatchAsync(
            imported,
            9,
            "quarantine-request",
            "{\"batchId\":9,\"selectedFileIds\":[999]}",
            CancellationToken.None);
        MaintenanceBatch? actual = await fixture.Repository.GetMaintenanceBatchAsync(imported.Id, CancellationToken.None);

        Assert.NotNull(actual);
        Assert.Equal(imported.Id, actual.Id);
        QuarantineItemRecord item = Assert.Single(actual.Items);
        Assert.Equal(999, item.FileId);
        Assert.Equal(QuarantineItemStatus.Quarantined, item.Status);
        Assert.Equal(imported.Items[0].ExpiresUtc, item.ExpiresUtc);
    }

    [Fact]
    public async Task ImportMaintenanceBatchAsync_ShouldRejectConflictingPersistedIdentity()
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        OperationRequest request = await fixture.Repository.CreateOperationRequestAsync(
            new OperationRequest
            {
                Kind = FontOperationKind.NormalizeNames,
                Status = OperationRequestStatus.Pending,
                IdempotencyKey = "existing-operation",
                RequestJson = RenameJson(fixture.RootPath),
                CreatedUtc = DateTimeOffset.UnixEpoch
            },
            CancellationToken.None);
        MaintenanceBatch existing = await fixture.Repository.SaveMaintenanceBatchAsync(
            new MaintenanceBatch
            {
                OperationRequestId = request.Id,
                Kind = FontOperationKind.NormalizeNames,
                Status = MaintenanceBatchStatus.Running,
                StartedUtc = DateTimeOffset.UnixEpoch,
                SummaryJson = "{\"faceScores\":[]}"
            },
            CancellationToken.None);
        var conflicting = new MaintenanceBatch
        {
            Id = existing.Id,
            OperationRequestId = 88,
            Kind = FontOperationKind.Quarantine,
            Status = MaintenanceBatchStatus.Partial,
            StartedUtc = DateTimeOffset.UnixEpoch.AddSeconds(1),
            SummaryJson = "{\"faceScores\":[]}",
            Items = []
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Repository.ImportMaintenanceBatchAsync(
            conflicting,
            9,
            "conflicting-request",
            "{\"batchId\":9,\"selectedFileIds\":[]}",
            CancellationToken.None));

        MaintenanceBatch? actual = await fixture.Repository.GetMaintenanceBatchAsync(existing.Id, CancellationToken.None);
        Assert.Equal(existing, actual);
    }

    [Fact]
    public async Task UpdateOperationAndMarkAnalysisStale_ShouldRemoveBothFromActiveQueries()
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        long generationId = await fixture.Repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);

        AnalysisBatch report = await fixture.Repository.PublishAnalysisAsync(
            new AnalysisBatch
            {
                GenerationId = generationId,
                Status = AnalysisBatchStatus.Complete,
                EvidenceVersion = 1,
                StartedUtc = DateTimeOffset.UnixEpoch,
                CompletedUtc = DateTimeOffset.UnixEpoch,
                SummaryJson = "{\"faceScores\":[]}"
            },
            CancellationToken.None);
        await fixture.Repository.CreateOperationRequestAsync(
            new OperationRequest
            {
                Kind = FontOperationKind.Quarantine,
                Status = OperationRequestStatus.Pending,
                AnalysisBatchId = report.Id,
                IdempotencyKey = "finish-me",
                RequestJson = "{}",
                CreatedUtc = DateTimeOffset.UnixEpoch
            },
            CancellationToken.None);
        OperationRequest? claimedRequest = await fixture.Repository.ClaimNextOperationAsync(FontOperationKind.Quarantine, CancellationToken.None);
        Assert.NotNull(claimedRequest);
        OperationRequest claimed = claimedRequest;

        await fixture.Repository.UpdateOperationAsync(
            claimed with { Status = OperationRequestStatus.Complete, CompletedUtc = DateTimeOffset.UnixEpoch.AddMinutes(1) },
            CancellationToken.None);
        await fixture.Repository.MarkAnalysisStaleAsync(generationId, CancellationToken.None);

        Assert.Null(await fixture.Repository.ClaimNextOperationAsync(FontOperationKind.Quarantine, CancellationToken.None));
        Assert.Null(await fixture.Repository.GetLatestCompleteAnalysisAsync(CancellationToken.None));
    }

    [Fact]
    public async Task PerformanceSamples_ShouldRoundTripNewestFirst()
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        var older = new PerformanceSample
        {
            Phase = "scan",
            Duration = TimeSpan.FromMilliseconds(20),
            ItemCount = 10,
            ReusedCount = 2,
            RecordedUtc = DateTimeOffset.UnixEpoch
        };
        var newer = older with { Phase = "analysis", RecordedUtc = DateTimeOffset.UnixEpoch.AddMinutes(1) };

        await fixture.Repository.SavePerformanceSampleAsync(older, CancellationToken.None);
        await fixture.Repository.SavePerformanceSampleAsync(newer, CancellationToken.None);
        IReadOnlyList<PerformanceSample> actual = await fixture.Repository.LoadRecentPerformanceSamplesAsync(10, CancellationToken.None);

        Assert.Equal(["analysis", "scan"], actual.Select(sample => sample.Phase));
    }

    private static async Task<T> ExecuteScalarAsync<T>(SqliteConnection connection, string commandText)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        object? value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return Assert.IsType<T>(Convert.ChangeType(value, typeof(T)));
    }

    private static FontFileRecord CreateFile(string path, string familyName)
    {
        return new FontFileRecord
        {
            Path = path,
            NormalizedPath = path.ToUpperInvariant(),
            PhysicalIdentity = path,
            Size = 128,
            ModifiedUtc = DateTimeOffset.UnixEpoch,
            Sha256 = new byte[32],
            Health = FontFileHealth.Healthy,
            Faces =
            [
                new FontFaceRecord
                {
                    FaceIndex = 0,
                    StrictFingerprint = new byte[32],
                    FamilyName = familyName,
                    StyleName = "Regular",
                    Names = [familyName],
                    Weight = 400,
                    Width = 5,
                    Technology = "TrueType"
                }
            ]
        };
    }

    private sealed class SqliteRepositoryFixture : IAsyncDisposable
    {
        private SqliteRepositoryFixture(string rootPath, FontDatabase database, SqliteFontRepository repository)
        {
            RootPath = rootPath;
            Database = database;
            Repository = repository;
        }

        public string RootPath { get; }

        public FontDatabase Database { get; }

        public SqliteFontRepository Repository { get; }

        public static async Task<SqliteRepositoryFixture> CreateAsync(string? pathSegment = null)
        {
            string root = Path.Join(Path.GetTempPath(), "ass-subsetter-tests", pathSegment ?? Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
            var paths = new FontStoragePaths(root, Path.Join(root, "catalog.db"), Path.Join(root, "quarantine"), Path.Join(root, "manifests"));
            var database = new FontDatabase(paths);
            var repository = new SqliteFontRepository(database);
            await repository.InitializeAsync(CancellationToken.None);
            return new SqliteRepositoryFixture(root, database, repository);
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            Directory.Delete(RootPath, recursive: true);
        }
    }
    [Fact]
    public async Task RenameAuthorizationRejectsActiveSourceAndTargetAcrossKeysAndAllowsAfterCompletion()
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        long generation = await fixture.Repository.BeginGenerationAsync(FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await fixture.Repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = generation, Status = AnalysisBatchStatus.Complete, SummaryJson = "{\"faceScores\":[]}" }, CancellationToken.None);
        var store = new FontOperationRequestStore(fixture.Repository, TimeProvider.System);
        var payload = FontOperationRequestPayloadCodec.ParseRenameExecution(RenameJson(fixture.RootPath));
        var first = await store.CreateRenameExecutionAsync(payload, "first", CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateRenameExecutionAsync(payload, "duplicate-source", CancellationToken.None));
        var other = FontOperationRequestPayloadCodec.ParseRenameExecution(RenameJson(fixture.RootPath, fileId: 2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateRenameExecutionAsync(other with { Items = [other.Items[0] with { NewPath = payload.Items[0].NewPath }] }, "duplicate-target", CancellationToken.None));
        Assert.Single(await fixture.Repository.LoadActiveRenameRequestsAsync(CancellationToken.None));
        Assert.Equal(first.Id, (await store.CreateRenameExecutionAsync(payload, "first", CancellationToken.None)).Id);
        await fixture.Repository.UpdateOperationAsync(first with { Status = OperationRequestStatus.Complete }, CancellationToken.None);
        Assert.NotEqual(first.Id, (await store.CreateRenameExecutionAsync(payload, "next", CancellationToken.None)).Id);
    }

    private static string RenameJson(string root, long generation = 1, long analysisId = 1, long fileId = 1)
    {
        var rules = new FontNamingRules(FontNamingRules.DefaultFileNameTemplate, "", false);
        string oldPath = Path.Combine(root, "source-" + fileId + ".ttf");
        var file = new FontFileRecord { Id = fileId, GenerationId = generation, Path = oldPath, NormalizedPath = oldPath, PhysicalIdentity = "file-" + fileId, Sha256 = new byte[32], Faces = [] };
        return FontOperationRequestPayloadCodec.CreateRenameExecutionJson(new(generation, analysisId, rules, rules.GetFingerprint(), [new RenameItemRecord { FileId = fileId, Evidence = file, OldPath = oldPath, NewPath = Path.Combine(root, "target-" + fileId + ".ttf"), SourceRoot = root }]));
    }

}
