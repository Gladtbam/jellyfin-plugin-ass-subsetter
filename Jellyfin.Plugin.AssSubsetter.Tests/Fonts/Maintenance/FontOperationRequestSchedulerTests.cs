using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;
using Jellyfin.Plugin.AssSubsetter.ScheduledTasks;
using Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Persistence;
using Jellyfin.Plugin.AssSubsetter.Tests.ScheduledTasks;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Maintenance;

public sealed class FontOperationRequestSchedulerTests
{
    [Fact]
    public async Task CandidateRefreshDispatchesDirectlyWithoutSynthesizingRenameTask()
    {
        var repository = new InMemoryFontRepository();
        var request = await CreateRequest(repository, FontOperationKind.RefreshRenameCandidates, "candidate-refresh");
        var manager = Manager(new RecordingTaskExecutor());
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scheduler = CreateScheduler(repository, manager.Object, () => false, async (kind, token) =>
        {
            Assert.Equal(FontOperationKind.RefreshRenameCandidates, kind);
            await repository.UpdateOperationAsync(request with { Status = OperationRequestStatus.Complete }, token);
            completed.TrySetResult();
            return new(null, "ready", request.Id);
        });
        await scheduler.StartAsync(CancellationToken.None);
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await scheduler.StopAsync(CancellationToken.None);
        Assert.Single(repository.OperationRequests);
        manager.Verify(item => item.QueueScheduledTask(It.IsAny<IScheduledTask>(), It.IsAny<TaskOptions>()), Times.Never);
    }

    [Fact]
    public async Task StartupWaitsForTaskRegistrationAndRetriesPersistedRequests()
    {
        var repository = new InMemoryFontRepository();
        var request = await CreateRequest(repository, FontOperationKind.NormalizeNames, "startup-rename");
        using var executor = CreateExecutor(repository);
        var manager = new Mock<ITaskManager>();
        IScheduledTaskWorker[] workers = [];
        manager.SetupGet(item => item.ScheduledTasks).Returns(() => workers);
        var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int prematureCalls = 0;
        using var scheduler = CreateScheduler(repository, manager.Object, () => false, async (kind, token) =>
        {
            if (workers.Length == 0) Interlocked.Increment(ref prematureCalls);
            var result = await executor.ExecutePendingOperationAsync(kind, new Progress<double>(), token);
            processed.TrySetResult();
            return result;
        });
        await scheduler.StartAsync(CancellationToken.None);
        Assert.Equal(0, prematureCalls);
        workers = Workers(executor);
        await processed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await scheduler.StopAsync(CancellationToken.None);
        Assert.Equal(0, prematureCalls);
        Assert.Equal(OperationRequestStatus.Complete, (await repository.GetOperationRequestAsync(request.Id, CancellationToken.None))!.Status);
        manager.Verify(item => item.QueueIfNotRunning<FontFilenameNormalizationTask>(), Times.Never);
    }

    [Theory]
    [InlineData(FontOperationKind.Analyze)]
    [InlineData(FontOperationKind.RebuildDerivedData)]
    [InlineData(FontOperationKind.Quarantine)]
    [InlineData(FontOperationKind.Purge)]
    [InlineData(FontOperationKind.NormalizeNames)]
    [InlineData(FontOperationKind.RollbackNames)]
    public async Task GateReleaseProcessesEveryPersistedTaskKind(FontOperationKind kind)
    {
        var repository = new InMemoryFontRepository();
        long? analysisBatchId = null;
        if (kind == FontOperationKind.Quarantine)
        {
            long generationId = await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
            await repository.WriteInventoryAsync(generationId, [new FontFileRecord
            {
                Id = 1,
                GenerationId = generationId,
                Path = "fixture.ttf",
                NormalizedPath = "fixture.ttf",
                PhysicalIdentity = "fixture",
                Sha256 = new byte[32],
                Faces = []
            }], CancellationToken.None);
            var analysis = await repository.PublishAnalysisAsync(new AnalysisBatch
            {
                GenerationId = generationId,
                Status = AnalysisBatchStatus.Complete,
                SummaryJson = "{}"
            }, CancellationToken.None);
            analysisBatchId = analysis.Id;
        }

        var request = await repository.CreateOperationRequestAsync(new OperationRequest
        {
            Kind = kind,
            IdempotencyKey = kind.ToString(),
            AnalysisBatchId = analysisBatchId,
            RequestJson = "{}"
        }, CancellationToken.None);
        using var executor = CreateExecutor(repository);
        var manager = Manager(executor);
        var processed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool busy = true;
        using var scheduler = CreateScheduler(repository, manager.Object, () => busy, async (pendingKind, token) =>
        {
            Assert.Equal(kind, pendingKind);
            var result = await executor.ExecutePendingOperationAsync(pendingKind, new Progress<double>(), token);
            processed.TrySetResult();
            return result;
        });
        await scheduler.StartAsync(CancellationToken.None);
        Assert.False(processed.Task.IsCompleted);
        busy = false;
        scheduler.Signal();
        await processed.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await scheduler.StopAsync(CancellationToken.None);
        Assert.Single(repository.OperationRequests);
        Assert.NotEqual(OperationRequestStatus.Pending, (await repository.GetOperationRequestAsync(request.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task MultipleRequestsDrainWithoutAnExtraTaskCompletionSignal()
    {
        var repository = new InMemoryFontRepository();
        await CreateRequest(repository, FontOperationKind.Analyze, "first-analysis");
        await CreateRequest(repository, FontOperationKind.RebuildDerivedData, "second-analysis");
        using var executor = CreateExecutor(repository);
        var manager = Manager(executor);
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int processed = 0;
        using var scheduler = CreateScheduler(repository, manager.Object, () => false, async (kind, token) =>
        {
            var result = await executor.ExecutePendingOperationAsync(kind, new Progress<double>(), token);
            if (Interlocked.Increment(ref processed) == 2) drained.TrySetResult();
            return result;
        });
        await scheduler.StartAsync(CancellationToken.None);
        await drained.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await scheduler.StopAsync(CancellationToken.None);
        Assert.Equal(2, processed);
        Assert.Equal(2, repository.OperationRequests.Count);
        Assert.All(repository.OperationRequests, request => Assert.Equal(OperationRequestStatus.Complete, request.Status));
    }

    [Fact]
    public async Task RequestConsumedAfterQueueSnapshotDoesNotAuthorizeMoreFilenameChanges()
    {
        var repository = new InMemoryFontRepository();
        var preview = await CreateRequest(repository, FontOperationKind.NormalizeNames, "rename-only");
        var normalizer = new RecordingFilenameNormalizer();
        using var executor = CreateExecutor(repository, normalizer);
        var manager = Manager(executor);
        var replayed = new TaskCompletionSource<FontTaskExecutionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scheduler = CreateScheduler(repository, manager.Object, () => false, async (kind, token) =>
        {
            // Another invocation consumed the last authorization after the scheduler read its snapshot.
            await repository.ClaimNextOperationAsync(FontOperationKind.NormalizeNames, token);
            await repository.UpdateOperationAsync(preview with { Status = OperationRequestStatus.Complete }, token);
            var result = await executor.ExecutePendingOperationAsync(kind, new Progress<double>(), token);
            replayed.TrySetResult(result);
            return result;
        });
        await scheduler.StartAsync(CancellationToken.None);
        var result = await replayed.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        await scheduler.StopAsync(CancellationToken.None);
        Assert.Equal("request-missing", result.Outcome);
        Assert.Equal(0, normalizer.LatestPlanRequestCalls);
        Assert.Equal(0, normalizer.ExecuteCalls);
        Assert.Single(repository.OperationRequests);
    }

    [Fact]
    public async Task StartupRecoveryPersistsAnalysisWithoutCallingTheUnregisteredTaskQueue()
    {
        var repository = new InMemoryFontRepository();
        var normalizer = new Mock<IFontFilenameNormalizationExecutor>();
        normalizer.Setup(item => item.ReconcileAsync(It.IsAny<CancellationToken>())).Returns(async (CancellationToken token) =>
        {
            await new FontOperationRequestStore(repository, TimeProvider.System).CreateTaskAsync(FontOperationKind.Analyze, "rename-refresh:startup", token);
        });
        var recovery = new FontRenameRecoveryService(repository, normalizer.Object);
        await recovery.StartAsync(CancellationToken.None);
        var request = Assert.Single(repository.OperationRequests);
        Assert.Equal(FontOperationKind.Analyze, request.Kind);
        Assert.Equal(OperationRequestStatus.Pending, request.Status);
        await recovery.StopAsync(CancellationToken.None);
    }

    private static Task<OperationRequest> CreateRequest(InMemoryFontRepository repository, FontOperationKind kind, string key)
        => repository.CreateOperationRequestAsync(new OperationRequest { Kind = kind, IdempotencyKey = key, RequestJson = "{}" }, CancellationToken.None);

    private static FontMaintenanceTaskExecutor CreateExecutor(InMemoryFontRepository repository, RecordingFilenameNormalizer? normalizer = null)
        => new(repository, new FontOperationRequestStore(repository, TimeProvider.System), TimeProvider.System,
            normalizer ?? new RecordingFilenameNormalizer(), (_, _, _) => Task.FromResult(new Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontInventoryScanResult(1, true, Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [])),
            (id, _, _) => Task.FromResult(new AnalysisBatch { Id = id, GenerationId = id, Status = AnalysisBatchStatus.Complete, SummaryJson = "{}" }),
            (request, _, _) => Task.FromResult(new MaintenanceBatch { Id = 1, OperationRequestId = request.Id, Kind = request.Kind, Status = MaintenanceBatchStatus.Complete, SummaryJson = "{}" }),
            (request, _) => Task.FromResult(new MaintenanceBatch { Id = 1, OperationRequestId = request.Id, Kind = request.Kind, Status = MaintenanceBatchStatus.Complete, SummaryJson = "{}" }),
            (_, _) => Task.FromResult(0), (batch, _, _) => Task.FromResult(batch with { Id = 1 }));

    private static FontOperationRequestScheduler CreateScheduler(InMemoryFontRepository repository, ITaskManager manager, Func<bool> isBusy, Func<FontOperationKind, CancellationToken, Task<FontTaskExecutionResult>> execute)
        => new(repository, manager, isBusy, execute, NullLogger<FontOperationRequestScheduler>.Instance);

    private static Mock<ITaskManager> Manager(IFontMaintenanceTaskExecutor executor)
    {
        var manager = new Mock<ITaskManager>();
        manager.SetupGet(item => item.ScheduledTasks).Returns(Workers(executor));
        return manager;
    }

    private static IScheduledTaskWorker[] Workers(IFontMaintenanceTaskExecutor executor)
        => [Worker(new FontCacheUpdateTask(executor, () => new Jellyfin.Plugin.AssSubsetter.Configuration.PluginConfiguration(), NullLogger<FontCacheUpdateTask>.Instance)),
            Worker(new FontDuplicateQuarantineTask(executor, NullLogger<FontDuplicateQuarantineTask>.Instance)),
            Worker(new FontFilenameNormalizationTask(executor, NullLogger<FontFilenameNormalizationTask>.Instance))];

    private static IScheduledTaskWorker Worker(IScheduledTask task)
    {
        var worker = new Mock<IScheduledTaskWorker>();
        worker.SetupGet(item => item.ScheduledTask).Returns(task);
        return worker.Object;
    }
}
