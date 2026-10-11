using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;
using Jellyfin.Plugin.AssSubsetter.ScheduledTasks;
using Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.AssSubsetter.Tests.ScheduledTasks;

public sealed class FontTaskMutualExclusionTests
{
    [Theory]
    [InlineData("analysis")]
    [InlineData("quarantine")]
    [InlineData("restore")]
    [InlineData("purge")]
    [InlineData("filenames")]
    [InlineData("history")]
    [InlineData("candidates")]
    public async Task ScheduledConflictsAreCancelledBeforeClaimingRequests(string activeOperation)
    {
        using var fixture = await Fixture.CreateAsync(activeOperation);
        Task running = fixture.Start(activeOperation);
        try
        {
            await fixture.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            OperationRequest[] before = fixture.Repository.OperationRequests.ToArray();
            IScheduledTask[] tasks =
            [
                new FontCacheUpdateTask(fixture.Executor, NullLogger<FontCacheUpdateTask>.Instance),
                new FontDuplicateQuarantineTask(fixture.Executor, NullLogger<FontDuplicateQuarantineTask>.Instance),
                new FontQuarantinePurgeTask(fixture.Executor, NullLogger<FontQuarantinePurgeTask>.Instance),
                new FontFilenameNormalizationTask(fixture.Executor, NullLogger<FontFilenameNormalizationTask>.Instance),
                new FontAnalysisBatchRetentionTask(fixture.Executor, () => new PluginConfiguration(), NullLogger<FontAnalysisBatchRetentionTask>.Instance)
            ];

            await Task.WhenAll(tasks.Select(task => Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => task.ExecuteAsync(new InlineProgress(_ => { }), CancellationToken.None)))).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.Equal(before, fixture.Repository.OperationRequests);
            Assert.True(fixture.Executor.IsBusy);
        }
        finally
        {
            fixture.Release.TrySetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }

        Assert.False(fixture.Executor.IsBusy);
    }

    [Theory]
    [InlineData("analysis")]
    [InlineData("quarantine")]
    [InlineData("restore")]
    [InlineData("purge")]
    [InlineData("filenames")]
    [InlineData("history")]
    [InlineData("candidates")]
    public async Task DurableConflictsRemainPendingAndCanExecuteAfterRelease(string activeOperation)
    {
        using var fixture = await Fixture.CreateAsync(activeOperation);
        Task running = fixture.Start(activeOperation);
        OperationRequest? purge = null;
        try
        {
            await fixture.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            FontOperationKind[] kinds =
            [
                FontOperationKind.Analyze, FontOperationKind.RebuildDerivedData, FontOperationKind.Quarantine,
                FontOperationKind.Restore, FontOperationKind.Purge,
                FontOperationKind.NormalizeNames, FontOperationKind.RollbackNames, FontOperationKind.RefreshRenameCandidates
            ];
            foreach (FontOperationKind kind in kinds)
            {
                OperationRequest request = await fixture.Repository.CreateOperationRequestAsync(new OperationRequest
                {
                    Kind = kind, IdempotencyKey = "deferred-" + kind, RequestJson = "{}"
                }, CancellationToken.None);
                FontTaskExecutionResult result = kind == FontOperationKind.Restore
                    ? await fixture.Executor.RestoreQuarantinedFontsAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)
                    : await fixture.Executor.ExecutePendingOperationAsync(kind, new InlineProgress(_ => { }), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                Assert.Null(result.OperationRequestId);
                Assert.Equal(OperationRequestStatus.Pending, (await fixture.Repository.GetOperationRequestAsync(request.Id, CancellationToken.None))!.Status);
                if (kind == FontOperationKind.Purge) purge = request;
            }
        }
        finally
        {
            fixture.Release.TrySetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }

        FontTaskExecutionResult completed = await fixture.Executor.ExecutePendingOperationAsync(
            FontOperationKind.Purge, new InlineProgress(_ => { }), CancellationToken.None);
        Assert.Equal(purge!.Id, completed.OperationRequestId);
        Assert.Equal(OperationRequestStatus.Complete, (await fixture.Repository.GetOperationRequestAsync(purge.Id, CancellationToken.None))!.Status);
    }

    private sealed class Fixture : IDisposable
    {
        public InMemoryFontRepository Repository { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FontMaintenanceTaskExecutor Executor { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync(string activeOperation)
        {
            var fixture = new Fixture();
            long generation = await fixture.Repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
            await fixture.Repository.WriteInventoryAsync(generation, [new FontFileRecord
            {
                Id = 1, GenerationId = generation, Path = "font.ttf", NormalizedPath = "FONT.TTF",
                PhysicalIdentity = "file-1", Sha256 = new byte[] { 1 }, Health = FontFileHealth.Healthy, Faces = []
            }], CancellationToken.None);

            AnalysisBatch analysis = await fixture.Repository.PublishAnalysisAsync(new AnalysisBatch
            {
                GenerationId = generation, Status = AnalysisBatchStatus.Complete, SummaryJson = "{}"
            }, CancellationToken.None);
            FontOperationKind kind = activeOperation switch
            {
                "quarantine" => FontOperationKind.Quarantine,
                "restore" => FontOperationKind.Restore,
                "filenames" => FontOperationKind.NormalizeNames,
                _ => FontOperationKind.Analyze
            };
            if (activeOperation is "quarantine" or "restore" or "filenames")
            {
                await fixture.Repository.CreateOperationRequestAsync(new OperationRequest
                {
                    Kind = kind, AnalysisBatchId = analysis.Id, IdempotencyKey = "active", RequestJson = "{}"
                }, CancellationToken.None);
            }

            var normalizer = new RecordingFilenameNormalizer
            {
                Execute = async request => { await fixture.BlockAsync(); return Batch(request); }
            };
            var namingRules = new FontNamingRules("{family}", "", false);
            var requirements = new FontRenameCandidateReadRequirements(namingRules.GetFingerprint(), "", "", analysis.EvidenceVersion, FontRenamePlanner.CurrentCandidateVersion);
            if (activeOperation == "candidates")
            {
                var queue = new FontRenameCandidateRefreshQueue(fixture.Repository, () => namingRules, () => requirements, TimeProvider.System);
                await queue.QueueAsync("active-candidates", CancellationToken.None);
            }

            fixture.Executor = new FontMaintenanceTaskExecutor(
                fixture.Repository, new FontOperationRequestStore(fixture.Repository, TimeProvider.System), TimeProvider.System,
                normalizer,
                async (_, _, _) => { await fixture.BlockAsync(); return new Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontInventoryScanResult(generation, true, Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, []); },
                (_, _, _) => Task.FromResult(analysis),
                async (request, _, _) => { await fixture.BlockAsync(); return Batch(request); },
                async (request, _) => { await fixture.BlockAsync(); return Batch(request); },
                async (_, _) => { await fixture.BlockAsync(); return 0; },
                (batch, _, _) => Task.FromResult(batch with { Id = 1 }),
                rebuildCandidates: async (id, token) =>
                {
                    await fixture.BlockAsync();
                    var state = (await fixture.Repository.GetRenameCandidateContextAsync(requirements, token)).State!;
                    var ready = state with { Status = FontRenameCandidateGenerationStatus.Ready };
                    await fixture.Repository.SaveRenameCandidatesAsync(new(ready, []), requirements, id, token);
                    return ready;
                },
                candidateRequirements: () => requirements);
            return fixture;
        }

        public Task Start(string operation) => Task.Run(() => operation switch
        {
            "candidates" => Executor.ExecutePendingOperationAsync(FontOperationKind.RefreshRenameCandidates, new InlineProgress(_ => { }), CancellationToken.None),
            "analysis" => (Task)Executor.RefreshInventoryAndAnalyzeAsync(new InlineProgress(_ => { }), CancellationToken.None),
            "quarantine" => Executor.QuarantineDuplicatesAsync(new InlineProgress(_ => { }), CancellationToken.None),
            "restore" => Executor.RestoreQuarantinedFontsAsync(CancellationToken.None),
            "purge" => Executor.PurgeExpiredQuarantineAsync(new InlineProgress(_ => { }), CancellationToken.None),
            "filenames" => Executor.NormalizeFilenamesAsync(new InlineProgress(_ => { }), CancellationToken.None),
            "history" => Executor.PruneHistoryAsync(1, new InlineProgress(value =>
            {
                if (value == 3) BlockAsync().GetAwaiter().GetResult();
            }), CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        });

        public void Dispose() => Executor.Dispose();

        private Task BlockAsync()
        {
            Entered.TrySetResult();
            return Release.Task;
        }

        private static MaintenanceBatch Batch(OperationRequest request) => new()
        {
            Id = 1, OperationRequestId = request.Id, Kind = request.Kind,
            Status = MaintenanceBatchStatus.Complete, SummaryJson = "{}"
        };
    }
}
