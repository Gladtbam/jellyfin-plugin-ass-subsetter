using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Persistence;

public sealed class SqliteRenameCandidateTests
{
    [Fact]
    public async Task Measure17543PhysicalCandidatesAndMixedDatabasePages()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await using var fixture = await Fixture.CreateAsync(17543);
        string root = Path.GetDirectoryName(fixture.Items[0].File.Path)!;
        var files = fixture.Items.Select((item, index) => item.File with
        {
            DirectoryPolicy = index % 7 == 0 ? FontDirectoryPolicy.Protected : FontDirectoryPolicy.Custom,
            Faces = [new FontFaceRecord { Id = item.File.Id * 10, FaceIndex = 0, StrictFingerprint = new byte[32], FamilyName = "Example", StyleName = "Regular", Names = ["Example"], Technology = "TrueType" }]
        }).ToArray();
        foreach (var file in files) File.WriteAllBytes(file.Path, new byte[128]);
        double setupMs = clock.Elapsed.TotalMilliseconds;
        var rules = new FontNamingRules("{originalName}-renamed", "", false);
        var requirements = fixture.Requirements with { RuleFingerprint = rules.GetFingerprint() };
        clock.Restart();
        var snapshot = new Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance.FontRenamePlanner(new Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance.PhysicalFontRenameDirectoryOperations())
            .Build(fixture.State.GenerationId, fixture.State.AnalysisBatchId, files, new HashSet<long>(), rules, new FontDirectoryPolicyResolver(root, null, null, false), TestContext.Current.CancellationToken);
        double generationMs = clock.Elapsed.TotalMilliseconds;
        Assert.Equal(17543, snapshot.Items.Count);
        Assert.Equal(15036, snapshot.Items.Count(item => item.SkipReason is null));
        clock.Restart();
        await fixture.Repository.SaveRenameCandidatesAsync(new(fixture.State with { Rules = rules, RuleFingerprint = rules.GetFingerprint() }, snapshot.Items), requirements, null, CancellationToken.None);
        double persistMs = clock.Elapsed.TotalMilliseconds;
        var store = new Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance.FontOperationRequestStore(fixture.Repository, TimeProvider.System);
        var eligible = snapshot.Items.Where(item => item.SkipReason is null).ToArray();
        for (int index = 0; index < 4; index++)
        {
            var item = CandidateItem(eligible[index]);
            var request = await store.CreateRenameExecutionAsync(new(fixture.State.GenerationId, fixture.State.AnalysisBatchId, rules, rules.GetFingerprint(), [item]), "measurement-" + index, CancellationToken.None);
            if (index == 1) await fixture.Repository.UpdateOperationAsync(request with { Status = OperationRequestStatus.Failed, Error = "measured failure" }, CancellationToken.None);
            if (index > 1)
            {
                await fixture.RecordOutcomeAsync(request, [item with { Status = index == 2 ? RenameItemStatus.Renamed : RenameItemStatus.RolledBack }]);
                await fixture.Repository.UpdateOperationAsync(request with { Status = OperationRequestStatus.Complete }, CancellationToken.None);
            }
        }

        await fixture.ReopenAsync();
        var pages = new List<object>();
        foreach (string status in new[] { "pending", "skipped", "failed", "renamed", "rolledback" })
        {
            clock.Restart();
            var first = await fixture.Repository.LoadRenameStatePageAsync(status, 1, 50, requirements, CancellationToken.None);
            double firstMs = clock.Elapsed.TotalMilliseconds;
            int lastPage = Math.Max(1, (int)Math.Ceiling(first.TotalCount / 50.0));
            foreach (int pageNumber in new[] { 1, 2, lastPage }.Distinct())
            {
                clock.Restart();
                var page = await fixture.Repository.LoadRenameStatePageAsync(status, pageNumber, 50, requirements, CancellationToken.None);
                double warmMs = clock.Elapsed.TotalMilliseconds;
                Assert.Equal(first.TotalCount, page.TotalCount);
                var expectedRows = Math.Clamp(first.TotalCount - ((long)pageNumber - 1) * 50, 0, 50);
                Assert.Equal(expectedRows, page.Items.Count);
                pages.Add(new { status, page = pageNumber, rows = page.Items.Count, total = page.TotalCount, firstReadMilliseconds = firstMs, warmReadMilliseconds = warmMs });
            }
        }

        var details = new { environment = System.Runtime.InteropServices.RuntimeInformation.OSDescription, fileCount = files.Length, syntheticFontMetadata = true, realTemporaryFilesystem = true, setupMilliseconds = setupMs, generationMilliseconds = generationMs, persistenceMilliseconds = persistMs, pages };
        string json = System.Text.Json.JsonSerializer.Serialize(details, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        TestContext.Current.TestOutputHelper?.WriteLine(json);
        string? output = Environment.GetEnvironmentVariable("ASS_RENAME_MEASUREMENT_OUTPUT");
        if (output is not null) await File.WriteAllTextAsync(output, json, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CandidateRoundTripSurvivesReopen()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveAsync();
        await fixture.ReopenAsync();
        var loaded = await fixture.Repository.LoadRenameCandidatesAsync(null, fixture.Requirements, CancellationToken.None);
        Assert.True(loaded.Context.IsCurrent);
        Assert.Equal(FontRenameCandidateGenerationStatus.Ready, loaded.Context.State!.Status);
        Assert.Equal(fixture.Items.Select(item => item.File.Id), loaded.Items.Select(item => item.File.Id));
        Assert.Equal("Example.ttf", Path.GetFileName(loaded.Items[0].NewPath));
        Assert.Equal(fixture.Items[0].File.PhysicalIdentity, loaded.Items[0].File.PhysicalIdentity);
        Assert.Equal(fixture.Items[0].File.Sha256.ToArray(), loaded.Items[0].File.Sha256.ToArray());
        Assert.Equal(fixture.State.Rules, loaded.Context.State.Rules);
        Assert.Equal(fixture.State.GeneratedUtc, loaded.Context.State.GeneratedUtc);
        Assert.Equal(8, SqliteSchema.CurrentVersion);
    }

    [Fact]
    public async Task EmptyReadySetIsNotMissing()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Repository.SaveRenameCandidatesAsync(new(fixture.State, []), fixture.Requirements, null, CancellationToken.None);
        var loaded = await fixture.Repository.LoadRenameCandidatesAsync(null, fixture.Requirements, CancellationToken.None);
        Assert.True(loaded.Context.IsCurrent);
        Assert.Equal(FontRenameCandidateGenerationStatus.Ready, loaded.Context.State!.Status);
        Assert.Empty(loaded.Items);
    }

    [Fact]
    public async Task ReplacementRollsBackOnInsertFailure()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveAsync();
        await fixture.ExecuteAsync("CREATE TRIGGER fail_candidate BEFORE INSERT ON rename_candidate BEGIN SELECT RAISE(ABORT, 'test insert failure'); END;");
        await Assert.ThrowsAsync<SqliteException>(() => fixture.Repository.SaveRenameCandidatesAsync(
            new(fixture.State with { GeneratedUtc = DateTimeOffset.UnixEpoch.AddDays(1) }, fixture.Items.Select(item => item with { NewPath = "changed.ttf" }).ToArray()),
            fixture.Requirements, null, CancellationToken.None));
        var loaded = await fixture.Repository.LoadRenameCandidatesAsync(null, fixture.Requirements, CancellationToken.None);
        Assert.Equal(fixture.State.GeneratedUtc, loaded.Context.State!.GeneratedUtc);
        Assert.Equal(fixture.Items.Select(item => item.NewPath), loaded.Items.Select(item => item.NewPath));
    }

    [Fact]
    public async Task OldRequestCannotReplaceNewerState()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveAsync();
        await fixture.Repository.SetRenameCandidateStateAsync(fixture.State with { Status = FontRenameCandidateGenerationStatus.Pending, OperationRequestId = 20 }, fixture.Requirements, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Repository.SaveRenameCandidatesAsync(
            new(fixture.State with { OperationRequestId = 10 }, []), fixture.Requirements, 10, CancellationToken.None));
        var current = await fixture.Repository.GetRenameCandidateContextAsync(fixture.Requirements, CancellationToken.None);
        Assert.Equal(20, current.State!.OperationRequestId);
        Assert.Equal(FontRenameCandidateGenerationStatus.Pending, current.State.Status);
    }

    [Fact]
    public async Task SelectionOverParameterLimitUsesOneSnapshot()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveAsync();
        var loaded = await fixture.Repository.LoadRenameCandidatesAsync(Enumerable.Range(1, 40000).Select(id => (long)id).ToArray(), fixture.Requirements, CancellationToken.None);
        Assert.True(loaded.Context.IsCurrent);
        Assert.Equal(fixture.Items.Select(item => item.File.Id), loaded.Items.Select(item => item.File.Id));
        Assert.Empty((await fixture.Repository.LoadRenameCandidatesAsync([], fixture.Requirements, CancellationToken.None)).Items);
    }

    [Fact]
    public async Task IncompatibleInputsOrStaleAnalysisDisableStoredCandidates()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveAsync();
        var mismatches = new[]
        {
            fixture.Requirements with { RuleFingerprint = "changed" },
            fixture.Requirements with { PolicyFingerprint = "changed" },
            fixture.Requirements with { EvidenceFingerprint = "changed" },
            fixture.Requirements with { CalculatorVersion = 2 },
            fixture.Requirements with { AnalysisEvidenceVersion = 2 }
        };
        foreach (var requirements in mismatches)
        {
            var loaded = await fixture.Repository.LoadRenameCandidatesAsync(null, requirements, CancellationToken.None);
            Assert.False(loaded.Context.IsCurrent);
            Assert.Empty(loaded.Items);
            Assert.Equal(FontRenameCandidateGenerationStatus.Stale, loaded.Context.State!.Status);
        }

        await fixture.Repository.MarkAnalysisStaleAsync(fixture.State.GenerationId, CancellationToken.None);
        Assert.False((await fixture.Repository.GetRenameCandidateContextAsync(fixture.Requirements, CancellationToken.None)).IsCurrent);
    }

    [Fact]
    public async Task PreviousPublicationCannotReplaceCandidates()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveAsync();
        long next = await fixture.Repository.BeginGenerationAsync(FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await fixture.Repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = next, Status = AnalysisBatchStatus.Complete, EvidenceVersion = 1, SummaryJson = "{\"faceScores\":[]}" }, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.SaveAsync());
    }

    [Fact]
    public async Task PageIncludesPendingRequestsWithoutBatchAndKeepsFailedReason()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveAsync();
        var item = CandidateItem(fixture.Items[0]);
        var store = new Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance.FontOperationRequestStore(fixture.Repository, TimeProvider.System);
        var request = await store.CreateRenameExecutionAsync(new(fixture.State.GenerationId, fixture.State.AnalysisBatchId, fixture.State.Rules, fixture.State.RuleFingerprint, [item]), "page-pending", CancellationToken.None);
        var pending = await fixture.Repository.LoadRenameStatePageAsync("pending", 1, 50, fixture.Requirements, CancellationToken.None);
        var row = Assert.Single(pending.Items);
        Assert.Equal(request.Id, row.OperationRequestId);
        Assert.Null(row.MaintenanceBatchId);
        Assert.False(row.CanExecute);
        await fixture.Repository.UpdateOperationAsync(request with { Status = OperationRequestStatus.Failed, Error = "specific failure" }, CancellationToken.None);
        var failed = await fixture.Repository.LoadRenameStatePageAsync("failed", 1, 50, fixture.Requirements, CancellationToken.None);
        Assert.Equal("specific failure", Assert.Single(failed.Items).Item.Error);
        Assert.Equal(1, failed.TotalCount);
        Assert.True(Assert.Single((await fixture.Repository.LoadRenameStatePageAsync("pending", 1, 50, fixture.Requirements, CancellationToken.None)).Items).CanExecute);
    }

    [Fact]
    public async Task Mixed17543RowsHaveExactStableDatabasePages()
    {
        await using var fixture = await Fixture.CreateAsync(17543);
        await fixture.SaveAsync();
        var store = new Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance.FontOperationRequestStore(fixture.Repository, TimeProvider.System);
        var activeItems = new[] { fixture.Items[20], fixture.Items[30], fixture.Items[40] };
        var active = await store.CreateRenameExecutionAsync(new(fixture.State.GenerationId, fixture.State.AnalysisBatchId, fixture.State.Rules, fixture.State.RuleFingerprint, activeItems.Select(CandidateItem).ToArray()), "large-active", CancellationToken.None);
        var failedItem = fixture.Items[80];
        var failed = await store.CreateRenameExecutionAsync(new(fixture.State.GenerationId, fixture.State.AnalysisBatchId, fixture.State.Rules, fixture.State.RuleFingerprint, [CandidateItem(failedItem)]), "large-failed", CancellationToken.None);
        await fixture.Repository.UpdateOperationAsync(failed with { Status = OperationRequestStatus.Failed, Error = "large failure" }, CancellationToken.None);
        var historyItem = CandidateItem(fixture.Items[101]);
        var history = await store.CreateRenameExecutionAsync(new(fixture.State.GenerationId, fixture.State.AnalysisBatchId, fixture.State.Rules, fixture.State.RuleFingerprint, [historyItem]), "large-history", CancellationToken.None);
        await fixture.RecordOutcomeAsync(history, [historyItem with { Status = RenameItemStatus.Renamed }]);
        await fixture.Repository.UpdateOperationAsync(history with { Status = OperationRequestStatus.Complete }, CancellationToken.None);
        var rollback = await fixture.Repository.CreateOperationRequestAsync(new OperationRequest { Kind = FontOperationKind.RollbackNames, IdempotencyKey = "large-rollback", RequestJson = "{}" }, CancellationToken.None);
        await fixture.RecordOutcomeAsync(rollback, [CandidateItem(fixture.Items[102]) with { Status = RenameItemStatus.RolledBack }]);
        Assert.Equal(1, (await fixture.Repository.LoadRenameStatePageAsync("renamed", 1, 50, fixture.Requirements, CancellationToken.None)).TotalCount);
        Assert.Equal(1, (await fixture.Repository.LoadRenameStatePageAsync("rolledback", 1, 50, fixture.Requirements, CancellationToken.None)).TotalCount);

        var expected = fixture.Items.Where(item => item.SkipReason is null).OrderBy(item => item.File.Path, StringComparer.Ordinal).ToArray();
        var secondPage = await fixture.Repository.LoadRenameStatePageAsync("pending", 2, 50, fixture.Requirements, CancellationToken.None);
        Assert.Equal(expected.LongLength, secondPage.TotalCount);
        Assert.Equal(expected.Skip(50).Take(50).Select(item => item.File.Id), secondPage.Items.Select(row => row.FileId));
        Assert.All(secondPage.Items, row => Assert.Equal(fixture.State.GenerationId, row.GenerationId));
        var firstPage = await fixture.Repository.LoadRenameStatePageAsync("pending", 1, 50, fixture.Requirements, CancellationToken.None);
        Assert.Equal(activeItems.Select(item => item.File.Id).Order(), firstPage.Items.Where(row => !row.CanExecute).Select(row => row.FileId).Order());
        Assert.All(firstPage.Items.Where(row => !row.CanExecute), row => Assert.Equal(active.Id, row.OperationRequestId));
        Assert.Equal(1, (await fixture.Repository.LoadRenameStatePageAsync("failed", 1, 50, fixture.Requirements, CancellationToken.None)).TotalCount);
        Assert.Equal(fixture.Items.LongCount(item => item.SkipReason is not null), (await fixture.Repository.LoadRenameStatePageAsync("skipped", 1, 50, fixture.Requirements, CancellationToken.None)).TotalCount);
        var stale = await fixture.Repository.LoadRenameStatePageAsync("pending", 1, 50, fixture.Requirements with { RuleFingerprint = "new rules" }, CancellationToken.None);
        Assert.False(stale.Context.IsCurrent);
        Assert.Equal(FontRenameCandidateGenerationStatus.Stale, stale.Context.State!.Status);
        Assert.Equal(3, stale.TotalCount);
    }

    [Fact]
    public async Task PageSnapshotDoesNotMixPublicationVersions()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveAsync();
        var expectedCounts = new System.Collections.Concurrent.ConcurrentDictionary<long, long>();
        expectedCounts[fixture.State.GenerationId] = 1;
        var initial = await fixture.Repository.LoadRenameStatePageAsync("pending", 1, 50, fixture.Requirements, CancellationToken.None);
        Assert.Equal(1, initial.TotalCount);
        var writer = Task.Run(async () =>
        {
            for (int iteration = 0; iteration < 8; iteration++)
            {
                long generation = await fixture.Repository.BeginGenerationAsync(FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
                await fixture.Repository.WriteInventoryAsync(generation, fixture.Items.Select(item => item.File with { Id = 0, GenerationId = generation }).ToArray(), CancellationToken.None);
                var files = await fixture.Repository.LoadInventoryAsync(generation, CancellationToken.None);
                expectedCounts[generation] = files.Count;
                var analysis = await fixture.Repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = generation, Status = AnalysisBatchStatus.Complete, EvidenceVersion = 1, SummaryJson = "{\"faceScores\":[]}" }, CancellationToken.None);
                var state = fixture.State with { GenerationId = generation, AnalysisBatchId = analysis.Id };
                await fixture.Repository.SaveRenameCandidatesAsync(new(state, files.Select((file, index) => fixture.Items[index] with { File = file, SkipReason = null }).ToArray()), fixture.Requirements, null, CancellationToken.None);
            }
        }, TestContext.Current.CancellationToken);
        do
        {
            var page = await fixture.Repository.LoadRenameStatePageAsync("pending", 1, 50, fixture.Requirements, CancellationToken.None);
            if (page.Context.IsCurrent)
            {
                Assert.Equal(expectedCounts[page.Context.Publication!.GenerationId], page.TotalCount);
                Assert.Equal(page.TotalCount, page.Items.Count);
                Assert.All(page.Items, row => Assert.Equal(page.Context.Publication.GenerationId, row.GenerationId));
            }
            else
            {
                Assert.Empty(page.Items);
            }
        }
        while (!writer.IsCompleted);
        await writer;
    }


    [Fact]
    public async Task SuccessfulHistoryKeepsExecutionIdentityAndDefersRollbackSafety()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveAsync();
        var item = CandidateItem(fixture.Items[0]);
        var store = new Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance.FontOperationRequestStore(fixture.Repository, TimeProvider.System);
        var request = await store.CreateRenameExecutionAsync(new(fixture.State.GenerationId, fixture.State.AnalysisBatchId, fixture.State.Rules, fixture.State.RuleFingerprint, [item]), "history", CancellationToken.None);
        var batch = await fixture.RecordOutcomeAsync(request, [item with { Status = RenameItemStatus.Renamed }]);
        await fixture.Repository.UpdateOperationAsync(request with { Status = OperationRequestStatus.Complete }, CancellationToken.None);
        var renamed = Assert.Single((await fixture.Repository.LoadRenameStatePageAsync("renamed", 1, 50, fixture.Requirements, CancellationToken.None)).Items);
        Assert.Equal(batch.Id, renamed.MaintenanceBatchId);
        Assert.Equal(request.Id, renamed.OperationRequestId);
        Assert.True(renamed.CanRollback);
        var rollback = await fixture.Repository.CreateOperationRequestAsync(new OperationRequest { Kind = FontOperationKind.RollbackNames, RequestJson = "{}", IdempotencyKey = "rollback-history" }, CancellationToken.None);
        await fixture.RecordOutcomeAsync(rollback, [item with { Status = RenameItemStatus.RolledBack }]);
        var after = Assert.Single((await fixture.Repository.LoadRenameStatePageAsync("renamed", 1, 50, fixture.Requirements, CancellationToken.None)).Items);
        Assert.Equal(renamed.Key, after.Key);
        Assert.False(after.CanRollback);
        Assert.Equal(rollback.Id, Assert.Single((await fixture.Repository.LoadRenameStatePageAsync("rolledback", 1, 50, fixture.Requirements, CancellationToken.None)).Items).OperationRequestId);
    }

    [Fact]
    public async Task HistoryDoesNotAttachToReplacementAtSamePath()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveAsync();
        var store = new Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance.FontOperationRequestStore(fixture.Repository, TimeProvider.System);
        var request = await store.CreateRenameExecutionAsync(new(fixture.State.GenerationId, fixture.State.AnalysisBatchId, fixture.State.Rules, fixture.State.RuleFingerprint, [CandidateItem(fixture.Items[0])]), "old-source", CancellationToken.None);
        await fixture.Repository.UpdateOperationAsync(request with { Status = OperationRequestStatus.Failed, Error = "old failure" }, CancellationToken.None);
        long generation = await fixture.Repository.BeginGenerationAsync(FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await fixture.Repository.WriteInventoryAsync(generation, [fixture.Items[0].File with { Id = 0, GenerationId = generation, PhysicalIdentity = "new file", Sha256 = Enumerable.Repeat((byte)7, 32).ToArray() }], CancellationToken.None);
        var replacement = Assert.Single(await fixture.Repository.LoadInventoryAsync(generation, CancellationToken.None));
        var analysis = await fixture.Repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = generation, Status = AnalysisBatchStatus.Complete, EvidenceVersion = 1, SummaryJson = "{\"faceScores\":[]}" }, CancellationToken.None);
        await fixture.Repository.SaveRenameCandidatesAsync(new(fixture.State with { GenerationId = generation, AnalysisBatchId = analysis.Id }, [fixture.Items[0] with { File = replacement }]), fixture.Requirements, null, CancellationToken.None);
        var pending = Assert.Single((await fixture.Repository.LoadRenameStatePageAsync("pending", 1, 50, fixture.Requirements, CancellationToken.None)).Items);
        Assert.Equal(replacement.Id, pending.FileId);
        Assert.Equal(generation, pending.GenerationId);
        Assert.Null(pending.OperationRequestId);
        Assert.True(pending.CanExecute);
        Assert.Equal(request.Id, Assert.Single((await fixture.Repository.LoadRenameStatePageAsync("failed", 1, 50, fixture.Requirements, CancellationToken.None)).Items).OperationRequestId);
    }

    [Fact]
    public async Task PageDoesNotParseSummaryOrUnselectedCandidateEvidence()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveAsync();
        await fixture.ExecuteAsync("UPDATE analysis_batch SET summary_json = 'unparsed'; UPDATE rename_candidate SET item_json = 'unparsed' WHERE category = 'skipped';");
        var page = await fixture.Repository.LoadRenameStatePageAsync("pending", -1, 1000, fixture.Requirements, CancellationToken.None);
        Assert.True(page.Context.IsCurrent);
        Assert.Single(page.Items);
        Assert.Equal(1, page.TotalCount);
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Repository.LoadRenameStatePageAsync("unknown", 1, 50, fixture.Requirements, CancellationToken.None));
    }


    [Fact]
    public async Task SqlPagePlanUsesCandidateAndRequestIndexes()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveAsync();
        var context = await fixture.Repository.GetRenameCandidateContextAsync(fixture.Requirements, CancellationToken.None);
        await using var connection = await fixture.Database.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        SqliteFontRepository.AddRenameStateParameters(command, "pending", context);
        command.CommandText = "EXPLAIN QUERY PLAN " + SqliteFontRepository.RenameStateRowsSql + "SELECT row_key FROM visible ORDER BY old_path, row_key LIMIT 50;";
        var plan = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken)) plan.Add(reader.GetString(3));
        Assert.Contains(plan, row => row.Contains("ix_rename_candidate_category_path", StringComparison.Ordinal));
        Assert.Contains(plan, row => row.Contains("rename_request_item", StringComparison.Ordinal) || row.Contains("sqlite_autoindex_rename_request_item", StringComparison.Ordinal));
        Assert.Contains(plan, row => row.Contains("SEARCH a USING", StringComparison.Ordinal));
        TestContext.Current.TestOutputHelper?.WriteLine(string.Join(Environment.NewLine, plan));
    }


    [Theory]
    [InlineData(OperationRequestStatus.Pending)]
    [InlineData(OperationRequestStatus.Running)]
    public async Task PendingAttemptWithErrorAppearsInFailedCategory(OperationRequestStatus status)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveAsync();
        var store = new Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance.FontOperationRequestStore(fixture.Repository, TimeProvider.System);
        var request = await store.CreateRenameExecutionAsync(new(fixture.State.GenerationId, fixture.State.AnalysisBatchId, fixture.State.Rules, fixture.State.RuleFingerprint, [CandidateItem(fixture.Items[0])]), "interrupted-attempt", CancellationToken.None);
        await fixture.Repository.UpdateOperationAsync(request with { Status = status, Error = "interrupted attempt" }, CancellationToken.None);
        var failed = await fixture.Repository.LoadRenameStatePageAsync("failed", 1, 50, fixture.Requirements, CancellationToken.None);
        Assert.Equal("interrupted attempt", Assert.Single(failed.Items).Item.Error);
        Assert.Equal(1, failed.TotalCount);
    }

    [Fact]
    public async Task AuthorizationUsesIndexedPathReservationsWithoutParsingOtherPayloads()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveAsync();
        var store = new Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance.FontOperationRequestStore(fixture.Repository, TimeProvider.System);
        var firstPayload = new FontRenameExecutionPayload(fixture.State.GenerationId, fixture.State.AnalysisBatchId, fixture.State.Rules, fixture.State.RuleFingerprint, [CandidateItem(fixture.Items[0])]);
        await store.CreateRenameExecutionAsync(firstPayload, "first-active", CancellationToken.None);
        await fixture.ExecuteAsync("UPDATE operation_request SET request_json = 'unparsed';");
        var second = await store.CreateRenameExecutionAsync(firstPayload with { Items = [CandidateItem(fixture.Items[1])] }, "other-source", CancellationToken.None);
        Assert.Equal(OperationRequestStatus.Pending, second.Status);
        var conflict = await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateRenameExecutionAsync(firstPayload, "same-source", CancellationToken.None));
        Assert.Contains("active filename authorization", conflict.Message);
    }


    [Fact]
    public async Task PublicationCommitsAnalysisAndCandidatesTogether()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveAsync();
        var (analysis, prepared) = await fixture.StageNextAsync();
        var published = await fixture.Repository.PublishAnalysisAsync(analysis, CancellationToken.None, prepared);
        var loaded = await fixture.Repository.LoadRenameCandidatesAsync(null, fixture.Requirements, CancellationToken.None);
        Assert.True(loaded.Context.IsCurrent);
        Assert.Equal(published.Id, loaded.Context.State!.AnalysisBatchId);
        Assert.Equal(analysis.GenerationId, loaded.Context.Publication!.GenerationId);
        Assert.Equal(prepared.Items.Select(item => item.File.Id), loaded.Items.Select(item => item.File.Id));
    }

    [Theory]
    [InlineData(FontRenameCandidateGenerationStatus.Failed)]
    [InlineData(FontRenameCandidateGenerationStatus.Stale)]
    public async Task CandidateFailureDoesNotBlockValidAnalysis(FontRenameCandidateGenerationStatus status)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveAsync();
        var (analysis, prepared) = await fixture.StageNextAsync();
        prepared = new(prepared.State with { Status = status, Error = "Invalid naming rules" }, []);
        var published = await fixture.Repository.PublishAnalysisAsync(analysis, CancellationToken.None, prepared);
        var loaded = await fixture.Repository.LoadRenameCandidatesAsync(null, fixture.Requirements, CancellationToken.None);
        Assert.Equal(published.Id, loaded.Context.Publication!.AnalysisBatchId);
        Assert.Equal(status, loaded.Context.State!.Status);
        Assert.Equal("Invalid naming rules", loaded.Context.State.Error);
        Assert.Empty(loaded.Items);
    }

    [Fact]
    public async Task ZeroFontPublicationProducesReadyEmptySet()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveAsync();
        var (analysis, prepared) = await fixture.StageNextAsync(empty: true);
        await fixture.Repository.PublishAnalysisAsync(analysis, CancellationToken.None, prepared);
        var loaded = await fixture.Repository.LoadRenameCandidatesAsync(null, fixture.Requirements, CancellationToken.None);
        Assert.True(loaded.Context.IsCurrent);
        Assert.Equal(FontRenameCandidateGenerationStatus.Ready, loaded.Context.State!.Status);
        Assert.Empty(loaded.Items);
    }

    [Fact]
    public async Task PublicationWithoutPreparationClearsOldCandidates()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.SaveAsync();
        var (analysis, _) = await fixture.StageNextAsync();
        await fixture.Repository.PublishAnalysisAsync(analysis, CancellationToken.None);
        var current = await fixture.Repository.GetRenameCandidateContextAsync(fixture.Requirements, CancellationToken.None);
        Assert.Equal(analysis.GenerationId, current.Publication!.GenerationId);
        Assert.Null(current.State);
    }


    private static RenameItemRecord CandidateItem(FontRenameCandidate item)
        => new() { FileId = item.File.Id, OldPath = item.File.Path, NewPath = item.NewPath!, Evidence = item.File, SourceRoot = item.SourceRoot!, NamingEvidence = item.NamingEvidence };

    internal sealed class Fixture : IAsyncDisposable
    {
        private readonly FontStoragePaths _paths;

        private Fixture(FontStoragePaths paths, FontDatabase database, SqliteFontRepository repository, FontRenameCandidateState state, FontRenameCandidate[] items)
        {
            _paths = paths;
            Database = database;
            Repository = repository;
            State = state;
            Items = items;
        }

        public FontDatabase Database { get; private set; }
        public SqliteFontRepository Repository { get; private set; }
        public FontRenameCandidateState State { get; }
        public FontRenameCandidate[] Items { get; }
        public FontRenameCandidateReadRequirements Requirements => new(State.RuleFingerprint, "", "", 1, 1);

        public static async Task<Fixture> CreateAsync(int count = 2)
        {
            string root = Path.Combine(Path.GetTempPath(), "ass-rename-candidates-tests", Guid.NewGuid().ToString("N"));
            var paths = new FontStoragePaths(root, Path.Combine(root, "catalog.db"), Path.Combine(root, "quarantine"), Path.Combine(root, "manifests"));
            var database = new FontDatabase(paths);
            var repository = new SqliteFontRepository(database);
            await repository.InitializeAsync(CancellationToken.None);
            long generation = await repository.BeginGenerationAsync(FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
            await repository.WriteInventoryAsync(generation, (count == 2 ? new[] { "a.ttf", "b.ttf" } : Enumerable.Range(0, count).Select(index => "source-" + index.ToString("D6") + ".ttf").ToArray()).Select(name => new FontFileRecord
            {
                Path = Path.Combine(root, name), NormalizedPath = Path.Combine(root, name), PhysicalIdentity = name, Size = 128,
                ModifiedUtc = DateTimeOffset.UnixEpoch, Sha256 = new byte[32], Health = FontFileHealth.Healthy, Faces = []
            }).ToArray(), CancellationToken.None);
            var analysis = await repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = generation, Status = AnalysisBatchStatus.Complete, EvidenceVersion = 1, SummaryJson = "{\"faceScores\":[]}" }, CancellationToken.None);
            var files = await repository.LoadInventoryAsync(generation, CancellationToken.None);
            var rules = new FontNamingRules(FontNamingRules.DefaultFileNameTemplate, "", false);
            var state = new FontRenameCandidateState(generation, analysis.Id, rules, rules.GetFingerprint(), 1, FontRenameCandidateGenerationStatus.Ready, DateTimeOffset.UnixEpoch, null, null);
            var items = files.Select((file, index) => new FontRenameCandidate { File = file, SourceRoot = root, NewPath = Path.Combine(root, count == 2 ? (index == 0 ? "Example.ttf" : "Second.ttf") : "Target-" + index.ToString("D6") + ".ttf"), SkipReason = count == 2 ? (index == 0 ? null : "test-skip") : (index % 7 == 0 ? "test-skip" : null) }).ToArray();
            return new(paths, database, repository, state, items);
        }

        public async Task<(AnalysisBatch Analysis, FontRenameCandidatePreparation Prepared)> StageNextAsync(bool empty = false)
        {
            long generation = await Repository.BeginGenerationAsync(FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
            await Repository.WriteInventoryAsync(generation, empty ? [] : Items.Select(item => item.File with { Id = 0, GenerationId = generation }).ToArray(), CancellationToken.None);
            var files = await Repository.LoadInventoryAsync(generation, CancellationToken.None);
            var analysis = new AnalysisBatch { GenerationId = generation, Status = AnalysisBatchStatus.Complete, EvidenceVersion = 1, SummaryJson = "{\"faceScores\":[]}" };
            var prepared = new FontRenameCandidatePreparation(State with { GenerationId = generation, AnalysisBatchId = 0 }, files.Select((file, index) => Items[index] with { File = file }).ToArray());
            return (analysis, prepared);
        }


        public async Task<MaintenanceBatch> RecordOutcomeAsync(OperationRequest request, RenameItemRecord[] items)
        {
            var batch = await Repository.SaveMaintenanceBatchAsync(new MaintenanceBatch { OperationRequestId = request.Id, Kind = request.Kind, Status = MaintenanceBatchStatus.Complete, SummaryJson = "{}", RenameItems = items }, CancellationToken.None);
            await Repository.UpdateMaintenanceBatchAsync(batch, CancellationToken.None);
            return batch;
        }

        public Task SaveAsync() => Repository.SaveRenameCandidatesAsync(new(State, Items), Requirements, null, CancellationToken.None);

        public async Task ExecuteAsync(string sql)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _paths.DatabasePath }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        public async Task ReopenAsync()
        {
            await Database.DisposeAsync();
            Database = new FontDatabase(_paths);
            Repository = new SqliteFontRepository(Database);
            await Repository.InitializeAsync(CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await Database.DisposeAsync();
            Directory.Delete(_paths.RootPath, true);
        }
    }
}
