using Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.ScheduledTasks;
using Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Persistence;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.AssSubsetter.Tests.ScheduledTasks;

public sealed partial class FontMaintenanceTaskTests
{
    [Fact]
    public void OnlyTheExistingInventoryTaskIdentityRemainsDiscoverable()
    {
        Type[] taskTypes =
        [
            .. typeof(FontCacheUpdateTask).Assembly.GetTypes()
                .Where(type => !type.IsAbstract && typeof(IScheduledTask).IsAssignableFrom(type))
        ];

        Assert.Equal(typeof(FontCacheUpdateTask), Assert.Single(taskTypes));
    }

    [Fact]
    public async Task ScheduledMaintenanceRunsAllEnabledStagesInOrderUnderOneLock()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var repository = new InMemoryFontRepository();
        long generation = await repository.BeginGenerationAsync(FontAnalysisInputSnapshot.Empty, [], token);
        await repository.WriteInventoryAsync(generation, [File(1, generation)], token);
        await repository.CreateOperationRequestAsync(new OperationRequest
        {
            Kind = FontOperationKind.NormalizeNames,
            IdempotencyKey = "scheduled-maintenance-rename",
            RequestJson = "{}"
        }, token);
        var phases = new List<string>();
        var progress = new List<double>();
        int releases = 0;
        repository.BeforePrune = _ => phases.Add("prune-history");
        repository.BeforeDatabaseOptimization = () =>
        {
            Assert.Equal(0, releases);
            Assert.All(progress, value => Assert.True(value < 100));
            phases.Add("optimize-database");
        };
        var normalizer = new RecordingFilenameNormalizer(request =>
        {
            Assert.Equal(0, releases);
            phases.Add("rename");
            return Maintenance(2, request.Id, FontOperationKind.NormalizeNames);
        });
        using var executor = CreateExecutor(
            repository,
            refresh: (_, _, _) => Task.FromResult(new FontInventoryScanResult(generation, true, FontAnalysisInputSnapshot.Empty, [])),
            analyze: (id, _) =>
            {
                phases.Add("analyze");
                return Task.FromResult(Analysis(id) with
                {
                    Groups = [new DuplicateGroupRecord
                    {
                        EvidenceJson = "{}",
                        Members = [new DuplicateGroupMemberRecord
                        {
                            FileId = 1, FaceId = 10, Decision = DuplicateGroupMemberDecision.QuarantineCandidate, ScoreJson = "{}", Reason = "duplicate"
                        }]
                    }]
                });
            },
            publish: (batch, cancellation) => repository.PublishAnalysisAsync(batch, cancellation),
            quarantine: (request, _, _) =>
            {
                Assert.Equal(0, releases);
                phases.Add("quarantine");
                return Task.FromResult(Maintenance(1, request.Id));
            },
            purge: _ =>
            {
                phases.Add("purge");
                return Task.FromResult(0);
            },
            filenameNormalizer: normalizer,
            signalPendingRequests: () => releases++);
        FontTaskExecutionResult result = await executor.RunScheduledMaintenanceAsync(
            FontMaintenanceStages.Analyze | FontMaintenanceStages.Quarantine | FontMaintenanceStages.Rename,
            3,
            new InlineProgress(progress.Add),
            token);

        Assert.Equal("Complete", result.Outcome);
        Assert.Equal(["analyze", "prune-history", "quarantine", "purge", "rename", "optimize-database"], phases);
        Assert.Equal(progress.Order(), progress);
        Assert.Equal(100, progress[^1]);
        Assert.Equal(1, releases);
        Assert.False(executor.IsBusy);
    }

    [Fact]
    public async Task ScheduledMaintenanceStopsBeforeLaterStagesWhenAnalysisFails()
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        var repository = new InMemoryFontRepository { BeforeDatabaseOptimization = () => throw new InvalidOperationException("The failed run must stop before database optimization.") };
        var normalizer = new RecordingFilenameNormalizer();
        using var executor = CreateExecutor(
            repository,
            analyze: (generation, _) => Task.FromResult(Analysis(generation) with { Status = AnalysisBatchStatus.Failed }),
            filenameNormalizer: normalizer);

        FontTaskExecutionResult result = await executor.RunScheduledMaintenanceAsync(
            FontMaintenanceStages.Analyze | FontMaintenanceStages.Rename,
            3,
            new InlineProgress(_ => { }),
            token);

        Assert.Equal("Failed", result.Outcome);
        Assert.Equal(0, normalizer.LatestPlanRequestCalls);
        Assert.False(executor.IsBusy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CombinedAnalysisHoldsLockThroughPruningAndPreservesCompletedRequest(bool failCleanup)
    {
        var repository = new InMemoryFontRepository();
        int signals = 0;
        using var executor = CreateExecutor(repository, signalPendingRequests: () => signals++);
        int pruned = 0;
        repository.BeforePrune = count =>
        {
            Assert.Equal(0, count);
            Assert.True(executor.IsBusy);
            Assert.Equal(0, signals);
            Assert.Equal(OperationRequestStatus.Complete, Assert.Single(repository.OperationRequests).Status);
            Assert.Equal("task-active", executor.ExecutePendingOperationAsync(FontOperationKind.Purge, new InlineProgress(_ => { }), CancellationToken.None).GetAwaiter().GetResult().Outcome);
            pruned++;
            if (failCleanup) throw new IOException("cleanup failed");
        };
        if (failCleanup)
            await Assert.ThrowsAsync<IOException>(() => executor.RunScheduledMaintenanceAsync(FontMaintenanceStages.Analyze, 0, new InlineProgress(_ => { }), CancellationToken.None));
        else
            Assert.Equal("analyzed", (await executor.RunScheduledMaintenanceAsync(FontMaintenanceStages.Analyze, 0, new InlineProgress(_ => { }), CancellationToken.None)).Outcome);
        Assert.Equal(1, pruned);
        Assert.Equal(1, signals);
        Assert.False(executor.IsBusy);
        Assert.Equal(OperationRequestStatus.Complete, Assert.Single(repository.OperationRequests).Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CombinedAnalysisSkipsPruningOnFailureOrCancellation(bool cancel)
    {
        var repository = new InMemoryFontRepository { BeforePrune = _ => Assert.Fail("Must not prune") };
        using var cts = new CancellationTokenSource();
        using var executor = CreateExecutor(repository, analyze: async (_, _) =>
        {
            if (cancel) { await cts.CancelAsync(); throw new OperationCanceledException(cts.Token); }
            return Analysis(1) with { Status = AnalysisBatchStatus.Failed };
        });
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.RunScheduledMaintenanceAsync(FontMaintenanceStages.Analyze, 1, new InlineProgress(_ => { }), cts.Token));
        else
            Assert.Equal("Failed", (await executor.RunScheduledMaintenanceAsync(FontMaintenanceStages.Analyze, 1, new InlineProgress(_ => { }), cts.Token)).Outcome);
        Assert.False(executor.IsBusy);
    }

    [Fact]
    public async Task CombinedAnalysisRejectsNegativeRetentionBeforeScanning()
    {
        using var executor = CreateExecutor(new InMemoryFontRepository(), refresh: (_, _, _) => throw new InvalidOperationException("Must not scan"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => executor.RunScheduledMaintenanceAsync(FontMaintenanceStages.Analyze, -1, new InlineProgress(_ => { }), CancellationToken.None));
    }

    [Fact]
    public async Task CombinedUnchangedAnalysisStillPrunes()
    {
        var repository = new InMemoryFontRepository();
        long generation = await repository.BeginGenerationAsync(FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await repository.PublishAnalysisAsync(Analysis(generation) with { EvidenceVersion = FontDuplicateAnalyzer.CurrentEvidenceVersion }, CancellationToken.None);
        int pruned = 0;
        repository.BeforePrune = _ => pruned++;
        using var executor = CreateExecutor(repository, refresh: (_, _, _) => Task.FromResult(new FontInventoryScanResult(generation, false, FontAnalysisInputSnapshot.Empty, [])));
        Assert.Equal("no-changes", (await executor.RunScheduledMaintenanceAsync(FontMaintenanceStages.Analyze, 1, new InlineProgress(_ => { }), CancellationToken.None)).Outcome);
        Assert.Equal(1, pruned);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CombinedQuarantinePurgesEmptyCandidatesButNotMissingAnalysis(bool hasAnalysis)
    {
        var repository = new InMemoryFontRepository();
        if (hasAnalysis)
        {
            long generation = await repository.BeginGenerationAsync(FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
            await repository.WriteInventoryAsync(generation, [File(1, generation)], CancellationToken.None);
            await repository.PublishAnalysisAsync(Analysis(generation), CancellationToken.None);
        }
        int purges = 0;
        FontMaintenanceTaskExecutor? executor = null;
        executor = CreateExecutor(repository, purge: _ => { Assert.True(executor!.IsBusy); purges++; return Task.FromResult(0); });
        using (executor)
        {
            var result = await executor.RunScheduledMaintenanceAsync(FontMaintenanceStages.Quarantine, 0, new InlineProgress(_ => { }), CancellationToken.None);
            Assert.Equal(hasAnalysis ? "no-candidates" : "analysis-missing", result.Outcome);
            Assert.Equal(hasAnalysis ? 1 : 0, purges);
        }
    }

    [Theory]
    [InlineData(MaintenanceBatchStatus.Complete, false)]
    [InlineData(MaintenanceBatchStatus.Complete, true)]
    [InlineData(MaintenanceBatchStatus.Failed, false)]
    [InlineData(MaintenanceBatchStatus.Cancelled, false)]
    [InlineData(MaintenanceBatchStatus.Partial, false)]
    public async Task CombinedQuarantinePreservesPrimaryStateAndOnlyPurgesAfterSuccess(MaintenanceBatchStatus status, bool purgeFails)
    {
        var repository = new InMemoryFontRepository();
        long generation = await repository.BeginGenerationAsync(FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await repository.WriteInventoryAsync(generation, [File(1, generation)], CancellationToken.None);
        var analysis = await repository.PublishAnalysisAsync(Analysis(generation), CancellationToken.None);
        var request = await repository.CreateOperationRequestAsync(new OperationRequest
        {
            Kind = FontOperationKind.Quarantine, AnalysisBatchId = analysis.Id,
            IdempotencyKey = "combined", RequestJson = "{}", Status = OperationRequestStatus.Pending
        }, CancellationToken.None);
        int purges = 0;
        using var executor = CreateExecutor(repository,
            quarantine: (operation, _, _) => Task.FromResult(Maintenance(1, operation.Id) with { Status = status }),
            purge: _ => { purges++; return purgeFails ? Task.FromException<int>(new IOException("purge failed")) : Task.FromResult(0); });
        if (purgeFails)
            await Assert.ThrowsAsync<IOException>(() => executor.RunScheduledMaintenanceAsync(FontMaintenanceStages.Quarantine, 0, new InlineProgress(_ => { }), CancellationToken.None));
        else
            Assert.Equal(status.ToString(), (await executor.RunScheduledMaintenanceAsync(FontMaintenanceStages.Quarantine, 0, new InlineProgress(_ => { }), CancellationToken.None)).Outcome);
        Assert.Equal(status == MaintenanceBatchStatus.Complete ? 1 : 0, purges);
        Assert.Equal(status == MaintenanceBatchStatus.Failed ? OperationRequestStatus.Failed : status == MaintenanceBatchStatus.Cancelled ? OperationRequestStatus.Cancelled : OperationRequestStatus.Complete,
            (await repository.GetOperationRequestAsync(request.Id, CancellationToken.None))!.Status);
        Assert.False(executor.IsBusy);
    }

    [Theory]
    [InlineData(MaintenanceBatchStatus.Complete)]
    [InlineData(MaintenanceBatchStatus.Failed)]
    [InlineData(MaintenanceBatchStatus.Cancelled)]
    [InlineData(MaintenanceBatchStatus.Partial)]
    public async Task RepeatedCombinedQuarantineCleansOnlyPreviouslySuccessfulWork(MaintenanceBatchStatus status)
    {
        var repository = new InMemoryFontRepository();
        long generation = await repository.BeginGenerationAsync(FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await repository.WriteInventoryAsync(generation, [File(10, generation)], CancellationToken.None);
        await repository.PublishAnalysisAsync(Analysis(generation) with
        {
            Groups = [new DuplicateGroupRecord
            {
                EvidenceJson = "{}", Members = [new DuplicateGroupMemberRecord
                {
                    FaceId = 100, FileId = 10, Decision = DuplicateGroupMemberDecision.QuarantineCandidate, ScoreJson = "{}", Reason = "duplicate"
                }]
            }]
        }, CancellationToken.None);
        int quarantines = 0, purges = 0;
        using var executor = CreateExecutor(repository,
            quarantine: (request, _, _) => { quarantines++; return repository.SaveMaintenanceBatchAsync(Maintenance(1, request.Id) with { Status = status }, CancellationToken.None); },
            purge: _ => { purges++; return Task.FromResult(0); });
        await executor.RunScheduledMaintenanceAsync(FontMaintenanceStages.Quarantine, 0, new InlineProgress(_ => { }), CancellationToken.None);
        await executor.RunScheduledMaintenanceAsync(FontMaintenanceStages.Quarantine, 0, new InlineProgress(_ => { }), CancellationToken.None);
        Assert.Equal(1, quarantines);
        Assert.Equal(status == MaintenanceBatchStatus.Complete ? 2 : 0, purges);
    }
}
