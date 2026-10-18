using Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.ScheduledTasks;
using Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Persistence;

namespace Jellyfin.Plugin.AssSubsetter.Tests.ScheduledTasks;

public sealed partial class FontMaintenanceTaskTests
{
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
            Assert.Equal("task-active", executor.PurgeExpiredQuarantineAsync(new InlineProgress(_ => { }), CancellationToken.None).GetAwaiter().GetResult().Outcome);
            pruned++;
            if (failCleanup) throw new IOException("cleanup failed");
        };
        if (failCleanup)
            await Assert.ThrowsAsync<IOException>(() => executor.RefreshAnalyzeAndPruneAsync(0, new InlineProgress(_ => { }), CancellationToken.None));
        else
            Assert.Equal("analyzed", (await executor.RefreshAnalyzeAndPruneAsync(0, new InlineProgress(_ => { }), CancellationToken.None)).Outcome);
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
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.RefreshAnalyzeAndPruneAsync(1, new InlineProgress(_ => { }), cts.Token));
        else
            Assert.Equal("Failed", (await executor.RefreshAnalyzeAndPruneAsync(1, new InlineProgress(_ => { }), cts.Token)).Outcome);
        Assert.False(executor.IsBusy);
    }

    [Fact]
    public async Task CombinedAnalysisRejectsNegativeRetentionBeforeScanning()
    {
        using var executor = CreateExecutor(new InMemoryFontRepository(), refresh: (_, _, _) => throw new InvalidOperationException("Must not scan"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => executor.RefreshAnalyzeAndPruneAsync(-1, new InlineProgress(_ => { }), CancellationToken.None));
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
        Assert.Equal("no-changes", (await executor.RefreshAnalyzeAndPruneAsync(1, new InlineProgress(_ => { }), CancellationToken.None)).Outcome);
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
            var result = await executor.QuarantineAndPurgeAsync(new InlineProgress(_ => { }), CancellationToken.None);
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
            await Assert.ThrowsAsync<IOException>(() => executor.QuarantineAndPurgeAsync(new InlineProgress(_ => { }), CancellationToken.None));
        else
            Assert.Equal(status.ToString(), (await executor.QuarantineAndPurgeAsync(new InlineProgress(_ => { }), CancellationToken.None)).Outcome);
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
        await executor.QuarantineAndPurgeAsync(new InlineProgress(_ => { }), CancellationToken.None);
        await executor.QuarantineAndPurgeAsync(new InlineProgress(_ => { }), CancellationToken.None);
        Assert.Equal(1, quarantines);
        Assert.Equal(status == MaintenanceBatchStatus.Complete ? 2 : 0, purges);
    }
}
