using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;
using Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;
using Jellyfin.Plugin.AssSubsetter.ScheduledTasks;
using Jellyfin.Plugin.AssSubsetter.Services;
using Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Persistence;
using Jellyfin.Plugin.AssSubsetter.Tests.Services;
using MediaBrowser.Controller;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.AssSubsetter.Tests.ScheduledTasks;

public sealed class FontMaintenanceTaskTests
{
    [Fact]
    public async Task CancellationDuringPreparationKeepsOldSet()
    {
        var repository = new InMemoryFontRepository();
        long old = await repository.BeginGenerationAsync(FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        var previous = await repository.PublishAnalysisAsync(Analysis(old), CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        using var executor = CreateExecutor(repository,
            prepare: (_, token) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled<FontRenameCandidatePreparation>(token);
            },
            validate: (_, _) => throw new InvalidOperationException("Validation must follow candidate preparation."),
            publish: (_, _) => throw new InvalidOperationException("Cancelled preparation must not publish."));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.RefreshInventoryAndAnalyzeAsync(new InlineProgress(_ => { }), cancellation.Token));
        Assert.Equal(previous.Id, (await repository.GetLatestCompleteAnalysisAsync(CancellationToken.None))!.Id);
        Assert.Equal(old, (await repository.GetDatabaseHealthAsync(CancellationToken.None)).PublishedGenerationId);
        Assert.Equal(OperationRequestStatus.Cancelled, Assert.Single(repository.OperationRequests).Status);
    }

    [Fact]
    public async Task UnchangedScanKeepsIdsAndRepairsMissingCandidates()
    {
        var repository = new InMemoryFontRepository();
        long generation = await repository.BeginGenerationAsync(FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        var current = await repository.PublishAnalysisAsync(Analysis(generation) with { EvidenceVersion = FontDuplicateAnalyzer.CurrentEvidenceVersion }, CancellationToken.None);
        var rules = new FontNamingRules(FontNamingRules.DefaultFileNameTemplate, "", false);
        var requirements = new FontRenameCandidateReadRequirements(rules.GetFingerprint(), "", "", FontDuplicateAnalyzer.CurrentEvidenceVersion, FontRenamePlanner.CurrentCandidateVersion);
        int repairs = 0;
        using var executor = CreateExecutor(repository,
            refresh: (_, _, _) => Task.FromResult(new FontInventoryScanResult(generation, false, FontAnalysisInputSnapshot.Empty, [])),
            analyze: (_, _) => throw new InvalidOperationException("No new analysis expected."),
            publish: (_, _) => throw new InvalidOperationException("No publication expected."),
            repairCandidates: async (id, token) =>
            {
                if ((await repository.GetRenameCandidateContextAsync(requirements, token)).IsCurrent) return;
                repairs++;
                var state = new FontRenameCandidateState(generation, current.Id, rules, rules.GetFingerprint(), FontRenamePlanner.CurrentCandidateVersion, FontRenameCandidateGenerationStatus.Pending, null, id, null);
                await repository.SetRenameCandidateStateAsync(state, requirements, token);
                await repository.SaveRenameCandidatesAsync(new(state with { Status = FontRenameCandidateGenerationStatus.Ready }, []), requirements, id, token);
            });
        for (int i = 0; i < 2; i++)
        {
            var result = await executor.RefreshInventoryAndAnalyzeAsync(new InlineProgress(_ => { }), CancellationToken.None);
            Assert.Equal(current.Id, result.BatchId);
            Assert.Equal("no-changes", result.Outcome);
        }

        Assert.Equal(1, repairs);
        Assert.True((await repository.GetRenameCandidateContextAsync(requirements, CancellationToken.None)).IsCurrent);
        Assert.Equal(generation, (await repository.GetDatabaseHealthAsync(CancellationToken.None)).PublishedGenerationId);
        Assert.Equal(current.Id, (await repository.GetLatestCompleteAnalysisAsync(CancellationToken.None))!.Id);
    }

    [Fact]
    public async Task CandidatePreparationMustPrecedeFinalInputValidation()
    {
        var repository = new InMemoryFontRepository();
        var calls = new List<string>();
        var rules = new FontNamingRules(FontNamingRules.DefaultFileNameTemplate, "", false);
        var prepared = new FontRenameCandidatePreparation(new(1, 0, rules, rules.GetFingerprint(), FontRenamePlanner.CurrentCandidateVersion, FontRenameCandidateGenerationStatus.Ready, DateTimeOffset.UtcNow, null, null), []);
        using var executor = CreateExecutor(repository,
            prepare: (_, _) => { calls.Add("prepare"); return Task.FromResult(prepared); },
            validate: (_, _) => { calls.Add("validate"); return Task.CompletedTask; },
            publishPrepared: (batch, received, _) =>
            {
                Assert.Same(prepared, received);
                calls.Add("publish");
                return Task.FromResult(batch);
            });
        await executor.RefreshInventoryAndAnalyzeAsync(new InlineProgress(_ => { }), CancellationToken.None);
        Assert.Equal(new[] { "prepare", "validate", "publish" }, calls);
    }

    [Theory]
    [InlineData(FontOperationKind.Analyze)]
    [InlineData(FontOperationKind.RebuildDerivedData)]
    [InlineData(FontOperationKind.Quarantine)]
    [InlineData(FontOperationKind.Purge)]
    [InlineData(FontOperationKind.NormalizeNames)]
    [InlineData(FontOperationKind.RollbackNames)]
    public async Task LatePendingReplayMustNotSynthesizeWorkWhenAuthorizationWasAlreadyConsumed(FontOperationKind kind)
    {
        var repository = new InMemoryFontRepository();
        var normalizer = new RecordingFilenameNormalizer();
        using var executor = CreateExecutor(repository, filenameNormalizer: normalizer);
        var result = await executor.ExecutePendingOperationAsync(kind, new InlineProgress(_ => { }), CancellationToken.None);
        Assert.Equal("request-missing", result.Outcome);
        Assert.Empty(repository.OperationRequests);
        Assert.Equal(0, normalizer.LatestPlanRequestCalls);
        Assert.Equal(0, normalizer.ExecuteCalls);
    }

    [Fact]
    public async Task UnchangedAnalysisRequest_ShouldReuseBatchWithoutAnalysisOrPublication()
    {
        var repository = new InMemoryFontRepository();
        long generation = await repository.BeginGenerationAsync(FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        AnalysisBatch current = await repository.PublishAnalysisAsync(Analysis(generation) with { EvidenceVersion = FontDuplicateAnalyzer.CurrentEvidenceVersion }, CancellationToken.None);

        using var executor = CreateExecutor(repository,
            refresh: (_, _, _) => Task.FromResult(new FontInventoryScanResult(generation, false, FontAnalysisInputSnapshot.Empty, [])),
            analyze: (_, _) => throw new InvalidOperationException("Unchanged scan must not analyze"),
            publish: (_, _) => throw new InvalidOperationException("Unchanged scan must not publish"));
        FontTaskExecutionResult result = await executor.RefreshInventoryAndAnalyzeAsync(new InlineProgress(_ => { }), CancellationToken.None);
        Assert.Equal("no-changes", result.Outcome);
        Assert.Equal(current.Id, result.BatchId);
        Assert.Equal(OperationRequestStatus.Complete, Assert.Single(repository.OperationRequests).Status);
    }

    [Theory]
    [InlineData("policy")]
    [InlineData("content")]
    [InlineData("cancel")]
    [InlineData("superseded")]
    public async Task UnchangedAnalysisRequestRejectsDriftBeforeReportingReuse(string drift)
    {
        string root = Path.Combine(Path.GetTempPath(), "reuse-drift-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "font.ttf");
            await System.IO.File.WriteAllBytesAsync(path, [1, 2, 3, 4], TestContext.Current.CancellationToken);
            var repository = new InMemoryFontRepository();
            string? protectedRoot = null;
            using var cancellation = new CancellationTokenSource();
            using var scanner = new FontInventoryScanner(repository, () => new FontDirectoryPolicyResolver(root, protectedRoot, null, false), new PathPhysicalFileIdentityProvider(), NullLogger<FontInventoryScanner>.Instance);
            var initial = await scanner.StageAsync(cancellationToken: CancellationToken.None);
            await repository.PublishAnalysisAsync(Analysis(initial.GenerationId) with { EvidenceVersion = FontDuplicateAnalyzer.CurrentEvidenceVersion }, CancellationToken.None);
            long expectedGeneration = initial.GenerationId;
            using var executor = CreateExecutor(repository,
                refresh: async (progress, force, token) =>
                {
                    var scan = await scanner.StageAsync(progress, force, token);
                    Assert.False(scan.HasChanges);
                    if (drift == "policy") protectedRoot = root;
                    if (drift == "content")
                    {
                        DateTime modified = System.IO.File.GetLastWriteTimeUtc(path);
                        await System.IO.File.WriteAllBytesAsync(path, [4, 3, 2, 1], CancellationToken.None);
                        System.IO.File.SetLastWriteTimeUtc(path, modified);
                    }
                    if (drift == "cancel") cancellation.Cancel();
                    if (drift == "superseded")
                    {
                        expectedGeneration = await repository.BeginGenerationAsync(scan.Input, [], CancellationToken.None);
                        await repository.WriteInventoryAsync(expectedGeneration, repository.PublishedFiles.Select(file => file with { GenerationId = expectedGeneration }).ToArray(), CancellationToken.None);
                        await repository.PublishAnalysisAsync(Analysis(expectedGeneration) with { EvidenceVersion = FontDuplicateAnalyzer.CurrentEvidenceVersion }, CancellationToken.None);
                    }
                    return scan;
                },
                analyze: (_, _) => throw new Exception("Unchanged input must not analyze"),
                publish: (_, _) => throw new Exception("Unchanged input must not publish"),
                validate: (scan, token) => scanner.ValidatePublicationInputsAsync(scan, token));
            if (drift == "cancel")
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.RefreshInventoryAndAnalyzeAsync(new InlineProgress(_ => { }), cancellation.Token));
            else
                await Assert.ThrowsAsync<InvalidOperationException>(() => executor.RefreshInventoryAndAnalyzeAsync(new InlineProgress(_ => { }), cancellation.Token));
            Assert.Equal(expectedGeneration, (await repository.GetDatabaseHealthAsync(CancellationToken.None)).PublishedGenerationId);
            var request = Assert.Single(repository.OperationRequests);
            Assert.Equal(drift == "cancel" ? OperationRequestStatus.Cancelled : OperationRequestStatus.Failed, request.Status);
            Assert.Null(request.ResultJson);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void RegisterServices_ShouldResolveTheSharedMaintenanceAndOperationSchedulingGraph()
    {
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, Mock.Of<IServerApplicationHost>());
        services.AddSingleton<IFontRepository>(new InMemoryFontRepository());
        services.AddSingleton(FontStoragePaths.Create(Path.GetTempPath()));
        services.AddSingleton<Func<PluginConfiguration>>(() => new());
        services.AddSingleton(Mock.Of<ITaskManager>());
        services.AddLogging();
        using ServiceProvider provider = services.BuildServiceProvider();

        var executor = provider.GetRequiredService<IFontMaintenanceTaskExecutor>();
        var scheduler = provider.GetRequiredService<FontOperationRequestScheduler>();
        Assert.Same(executor, provider.GetRequiredService<IFontMaintenanceTaskExecutor>());
        Assert.Same(scheduler, provider.GetRequiredService<IFontOperationRequestDispatcher>());
        Assert.NotNull(ActivatorUtilities.CreateInstance<Jellyfin.Plugin.AssSubsetter.Fonts.Api.FontManagementController>(provider));
    }

    [Fact]
    public async Task RegisterServices_ShouldResolveProductionCacheManagerWithoutConstructorAmbiguity()
    {
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, Mock.Of<IServerApplicationHost>());
        var repository = new InMemoryFontRepository();
        services.AddSingleton<IFontRepository>(repository);
        services.AddSingleton<Func<Jellyfin.Plugin.AssSubsetter.Configuration.PluginConfiguration>>(() => new());
        services.AddLogging();
        using ServiceProvider provider = services.BuildServiceProvider();

        FontCacheManager manager = provider.GetRequiredService<FontCacheManager>();

        Assert.Same(manager, provider.GetRequiredService<FontCacheManager>());
        Assert.Null(await FontCacheManagerFixture.LookupAsync(manager, new Jellyfin.Plugin.AssSubsetter.Models.FontDescriptor("Cold Font", null, false, false)));
        Assert.Equal(0, repository.InitializeCallCount);
    }

    [Fact]
    public void RegisterServices_ShouldResolveStartupServiceWithoutConstructorAmbiguity()
    {
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, Mock.Of<IServerApplicationHost>());
        services.AddSingleton<IFontRepository>(new InMemoryFontRepository());
        services.AddSingleton(FontStoragePaths.Create(Path.GetTempPath()));
        services.AddSingleton<Func<Jellyfin.Plugin.AssSubsetter.Configuration.PluginConfiguration>>(() => new());
        services.AddLogging();
        using ServiceProvider provider = services.BuildServiceProvider();

        FontRuntimeSnapshotStartupService startup = ActivatorUtilities.CreateInstance<FontRuntimeSnapshotStartupService>(provider);

        Assert.NotNull(startup);
    }

    [Fact]
    public void DefaultTriggers_ShouldMatchApprovedSchedule()
    {
        var executor = new RecordingTaskExecutor();

        Assert.Empty(new FontDuplicateQuarantineTask(executor, NullLogger<FontDuplicateQuarantineTask>.Instance).GetDefaultTriggers());
        AssertDailyWithoutStartup(new FontQuarantinePurgeTask(executor, NullLogger<FontQuarantinePurgeTask>.Instance));
        AssertDailyWithoutStartup(new FontAnalysisBatchRetentionTask(
            executor,
            () => new PluginConfiguration(),
            NullLogger<FontAnalysisBatchRetentionTask>.Instance));
        Assert.Empty(new FontFilenameNormalizationTask(executor, NullLogger<FontFilenameNormalizationTask>.Instance).GetDefaultTriggers());
    }

    [Fact]
    public async Task QuarantineTask_ShouldForwardPerFileProgress()
    {
        var executor = new Mock<IFontMaintenanceTaskExecutor>();
        executor.Setup(item => item.QuarantineDuplicatesAsync(It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
            .Returns((IProgress<double> progress, CancellationToken _) =>
            {
                progress.Report(37);
                return Task.FromResult(new FontTaskExecutionResult(1, "Partial"));
            });
        var reported = new List<double>();
        var task = new FontDuplicateQuarantineTask(
            executor.Object,
            NullLogger<FontDuplicateQuarantineTask>.Instance);

        await task.ExecuteAsync(new InlineProgress(reported.Add), CancellationToken.None);

        Assert.Contains(37d, reported);
        Assert.Equal(100d, reported[^1]);
    }

    [Fact]
    public void TaskTypes_ShouldBeDiscoverableWithStableDistinctKeys()
    {
        var executor = new RecordingTaskExecutor();
        IScheduledTask[] tasks =
        [
            new FontCacheUpdateTask(executor, NullLogger<FontCacheUpdateTask>.Instance),
            new FontAnalysisBatchRetentionTask(
                executor,
                () => new PluginConfiguration(),
                NullLogger<FontAnalysisBatchRetentionTask>.Instance),
            new FontDuplicateQuarantineTask(executor, NullLogger<FontDuplicateQuarantineTask>.Instance),
            new FontQuarantinePurgeTask(executor, NullLogger<FontQuarantinePurgeTask>.Instance),
            new FontFilenameNormalizationTask(executor, NullLogger<FontFilenameNormalizationTask>.Instance)
        ];

        Assert.Equal(tasks.Length, tasks.Select(task => task.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.All(tasks, task => Assert.False(string.IsNullOrWhiteSpace(task.Name)));
        Assert.All(tasks, task => Assert.False(string.IsNullOrWhiteSpace(task.Description)));
        Type[] discoverable = typeof(FontCacheUpdateTask).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && typeof(IScheduledTask).IsAssignableFrom(type))
            .ToArray();
        Assert.All(tasks, task => Assert.Contains(task.GetType(), discoverable));
        Assert.DoesNotContain(discoverable, type => type.Name == "FontQuarantineRestoreTask");
    }

    [Fact]
    public void PluginServiceRegistrator_ShouldRegisterOneSingletonMaintenanceGraph()
    {
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, Mock.Of<IServerApplicationHost>());

        AssertSingleton<FontDatabase>(services);
        AssertSingleton<IFontRepository>(services);
        AssertSingleton<IFontAnalysisEvidenceSource>(services);
        AssertSingleton<FontDuplicateAnalyzer>(services);
        AssertSingleton<FontOperationRequestStore>(services);
        AssertSingleton<FontQuarantineManager>(services);
        AssertSingleton<IFontFilenameNormalizationExecutor>(services);
        AssertSingleton<IFontMaintenanceTaskExecutor>(services);
        AssertSingleton<FontRestoreRequestWorker>(services);
        AssertSingleton<IFontRestoreRequestDispatcher>(services);
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationFactory is not null);
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(FontRuntimeSnapshotStartupService));
    }

    [Fact]
    public async Task MaintenanceTasks_ShouldDelegateExactlyOnce()
    {
        var executor = new RecordingTaskExecutor();
        var progress = new InlineProgress(_ => { });

        await new FontDuplicateQuarantineTask(executor, NullLogger<FontDuplicateQuarantineTask>.Instance)
            .ExecuteAsync(progress, CancellationToken.None);
        await new FontQuarantinePurgeTask(executor, NullLogger<FontQuarantinePurgeTask>.Instance)
            .ExecuteAsync(progress, CancellationToken.None);
        await new FontFilenameNormalizationTask(executor, NullLogger<FontFilenameNormalizationTask>.Instance)
            .ExecuteAsync(progress, CancellationToken.None);

        Assert.Equal(1, executor.QuarantineCalls);
        Assert.Equal(1, executor.PurgeCalls);
        Assert.Equal(1, executor.NormalizeCalls);
    }

    [Fact]
    public async Task RefreshAndAnalyzeAsync_ShouldStageAnalyzeThenPublishInOrder()
    {
        var calls = new List<string>();
        var repository = new InMemoryFontRepository();
        var executor = CreateExecutor(
            repository,
            refresh: (_, _, _) =>
            {
                calls.Add("refresh");
                return Task.FromResult(new FontInventoryScanResult(7, true, FontAnalysisInputSnapshot.Empty, []));
            },
            analyze: (generationId, _) =>
            {
                calls.Add("analyze:" + generationId);
                return Task.FromResult(Analysis(generationId, 91));
            },
            publish: (generationId, _) =>
            {
                calls.Add("publish:" + generationId.GenerationId);
                return Task.FromResult(generationId with { Id = generationId.Id == 0 ? 1 : generationId.Id });
            });

        FontTaskExecutionResult result = await executor.RefreshInventoryAndAnalyzeAsync(
            new InlineProgress(_ => { }),
            CancellationToken.None);

        Assert.Equal(["refresh", "analyze:7", "publish:7"], calls);
        Assert.Equal(91, result.BatchId);
    }

    [Fact]
    public async Task RefreshAndAnalyzeAsync_ShouldScaleAnalysisProgressAfterInventory()
    {
        var repository = new InMemoryFontRepository();
        var values = new List<double>();
        var executor = CreateExecutor(
            repository,
            refresh: (progress, _, _) =>
            {
                progress?.Report(100);
                return Task.FromResult(new FontInventoryScanResult(7, true, FontAnalysisInputSnapshot.Empty, []));
            },
            analyzeWithProgress: (generationId, progress, _) =>
            {
                progress?.Report(50);
                return Task.FromResult(Analysis(generationId, 91));
            });

        await executor.RefreshInventoryAndAnalyzeAsync(new InlineProgress(values.Add), CancellationToken.None);

        Assert.Contains(60, values);
        Assert.Contains(72.5, values);
        Assert.Equal(100, values[^1]);
    }
    [Fact]
    public async Task DisposeDuringAnalysis_ShouldNotReplaceTaskCompletionWithGateDisposalFailure()
    {
        var repository = new InMemoryFontRepository();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = CreateExecutor(
            repository,
            refresh: (_, _, _) => Task.FromResult(new FontInventoryScanResult(7, true, FontAnalysisInputSnapshot.Empty, [])),
            analyze: async (generationId, _) =>
            {
                entered.SetResult();
                await release.Task;
                return Analysis(generationId, 91);
            });

        Task<FontTaskExecutionResult> operation = executor.RefreshInventoryAndAnalyzeAsync(new InlineProgress(_ => { }), CancellationToken.None);
        await entered.Task;
        executor.Dispose();
        release.SetResult();

        FontTaskExecutionResult result = await operation;
        Assert.Equal(91, result.BatchId);
    }

    [Fact]
    public async Task RefreshAndAnalyzeAsync_ShouldRecoverHistoricalIdsBeforeAllocatingTaskRequest()
    {
        var repository = new InMemoryFontRepository();
        bool recovered = false;
        var normalizer = new RecordingFilenameNormalizer
        {
            Recovery = () =>
            {
                Assert.Empty(repository.OperationRequests);
                recovered = true;
            }
        };
        using var executor = CreateExecutor(repository, filenameNormalizer: normalizer, refresh: (_, _, _) =>
        {
            Assert.True(recovered);
            return Task.FromResult(new FontInventoryScanResult(1L, true, FontAnalysisInputSnapshot.Empty, []));
        });
        await executor.RefreshInventoryAndAnalyzeAsync(new InlineProgress(_ => { }), CancellationToken.None);
        Assert.Single(repository.OperationRequests);
    }

    [Fact]
    public async Task RefreshAndAnalyzeAsync_ShouldClaimAndCompleteAnExplicitAnalyzeRequest()
    {
        var repository = new InMemoryFontRepository();
        OperationRequest request = await CreateTaskRequestAsync(repository, FontOperationKind.Analyze, "explicit-analyze");
        var executor = CreateExecutor(
            repository,
            refresh: (_, _, _) => Task.FromResult(new FontInventoryScanResult(7, true, FontAnalysisInputSnapshot.Empty, [])),
            analyze: (generationId, _) => Task.FromResult(Analysis(generationId, 91)));

        FontTaskExecutionResult result = await executor.RefreshInventoryAndAnalyzeAsync(
            new InlineProgress(_ => { }),
            CancellationToken.None);

        OperationRequest persisted = Assert.Single(repository.OperationRequests);
        Assert.Equal(request.Id, result.OperationRequestId);
        Assert.Equal(91, result.BatchId);
        Assert.Equal(OperationRequestStatus.Complete, persisted.Status);
        Assert.NotNull(persisted.ClaimedUtc);
        Assert.NotNull(persisted.CompletedUtc);
    }

    [Fact]
    public async Task RefreshAndAnalyzeAsync_ShouldConsumeARebuildDerivedDataRequest()
    {
        var repository = new InMemoryFontRepository();
        OperationRequest request = await CreateTaskRequestAsync(repository, FontOperationKind.RebuildDerivedData, "rebuild-request");
        var executor = CreateExecutor(repository, refresh: (_, _, _) => Task.FromResult(new FontInventoryScanResult(1L, true, FontAnalysisInputSnapshot.Empty, [])));

        FontTaskExecutionResult result = await executor.RefreshInventoryAndAnalyzeAsync(new InlineProgress(_ => { }), CancellationToken.None);

        OperationRequest persisted = Assert.Single(repository.OperationRequests);
        Assert.Equal(request.Id, result.OperationRequestId);
        Assert.Equal(OperationRequestStatus.Complete, persisted.Status);
    }

    [Fact]
    public async Task RefreshAndAnalyzeAsync_ShouldSynthesizeClaimAndCompleteOneAnalyzeRequest()
    {
        var repository = new InMemoryFontRepository();
        var executor = CreateExecutor(
            repository,
            refresh: (_, _, _) => Task.FromResult(new FontInventoryScanResult(7, true, FontAnalysisInputSnapshot.Empty, [])),
            analyze: (generationId, _) => Task.FromResult(Analysis(generationId, 91)));

        FontTaskExecutionResult result = await executor.RefreshInventoryAndAnalyzeAsync(
            new InlineProgress(_ => { }),
            CancellationToken.None);

        OperationRequest persisted = Assert.Single(repository.OperationRequests);
        Assert.Equal(FontOperationKind.Analyze, persisted.Kind);
        Assert.Equal(persisted.Id, result.OperationRequestId);
        Assert.Equal(OperationRequestStatus.Complete, persisted.Status);
    }

    [Fact]
    public async Task RefreshAndAnalyzeAsync_ShouldFailTheClaimedRequestWhenAnalysisFails()
    {
        var repository = new InMemoryFontRepository();
        await CreateTaskRequestAsync(repository, FontOperationKind.Analyze, "failing-analyze");
        var executor = CreateExecutor(
            repository,
            refresh: (_, _, _) => Task.FromResult(new FontInventoryScanResult(7, true, FontAnalysisInputSnapshot.Empty, [])),
            analyze: (_, _) => Task.FromException<AnalysisBatch>(new InvalidOperationException("analysis failure")));

        await Assert.ThrowsAsync<InvalidOperationException>(() => executor.RefreshInventoryAndAnalyzeAsync(
            new InlineProgress(_ => { }),
            CancellationToken.None));

        OperationRequest persisted = Assert.Single(repository.OperationRequests);
        Assert.Equal(OperationRequestStatus.Failed, persisted.Status);
        Assert.Equal("analysis failure", persisted.Error);
    }

    [Fact]
    public async Task RefreshAndAnalyzeAsync_ShouldCancelTheClaimedRequestAndRetainItsIdentifier()
    {
        var repository = new InMemoryFontRepository();
        OperationRequest request = await CreateTaskRequestAsync(repository, FontOperationKind.Analyze, "cancelled-analyze");
        using var cancellation = new CancellationTokenSource();
        var executor = CreateExecutor(repository, refresh: (_, _, _) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<FontInventoryScanResult>(cancellation.Token);
        });

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => executor.RefreshInventoryAndAnalyzeAsync(new InlineProgress(_ => { }), cancellation.Token));

        OperationRequest persisted = Assert.Single(repository.OperationRequests);
        Assert.Equal(request.Id, Assert.IsType<long>(exception.Data[FontScheduledTaskExecution.OperationRequestIdDataKey]));
        Assert.Equal(OperationRequestStatus.Cancelled, persisted.Status);
    }

    [Fact]
    public async Task RefreshAndAnalyzeAsync_ShouldFailTheClaimedRequestWhenTheAnalysisBatchIsNotComplete()
    {
        var repository = new InMemoryFontRepository();
        await CreateTaskRequestAsync(repository, FontOperationKind.Analyze, "stale-analyze");
        var executor = CreateExecutor(
            repository,
            refresh: (_, _, _) => Task.FromResult(new FontInventoryScanResult(7, true, FontAnalysisInputSnapshot.Empty, [])),
            analyze: (generationId, _) => Task.FromResult(Analysis(generationId, 91) with { Status = AnalysisBatchStatus.Stale }));

        FontTaskExecutionResult result = await executor.RefreshInventoryAndAnalyzeAsync(
            new InlineProgress(_ => { }),
            CancellationToken.None);

        Assert.Equal("Stale", result.Outcome);
        Assert.Equal(OperationRequestStatus.Failed, Assert.Single(repository.OperationRequests).Status);
    }

    [Fact]
    public async Task QuarantineDuplicatesAsync_ShouldSkipWhenAnalysisIsMissingOrStale()
    {
        var repository = new InMemoryFontRepository();
        int quarantineCalls = 0;
        var executor = CreateExecutor(
            repository,
            quarantine: (_, _, _) =>
            {
                quarantineCalls++;
                return Task.FromResult(Maintenance(1));
            });

        FontTaskExecutionResult missing = await executor.QuarantineDuplicatesAsync(new InlineProgress(_ => { }), CancellationToken.None);

        long generationId = await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await repository.WriteInventoryAsync(generationId, [File(10, generationId)], CancellationToken.None);

        await repository.PublishAnalysisAsync(Analysis(generationId), CancellationToken.None);
        await repository.MarkAnalysisStaleAsync(generationId, CancellationToken.None);
        FontTaskExecutionResult stale = await executor.QuarantineDuplicatesAsync(new InlineProgress(_ => { }), CancellationToken.None);

        Assert.Equal("analysis-missing", missing.Outcome);
        Assert.Equal("analysis-missing", stale.Outcome);
        Assert.Equal(0, quarantineCalls);
    }

    [Fact]
    public async Task QuarantineDuplicatesAsync_ShouldSkipWhileInventoryAnalysisIsActive()
    {
        var repository = new InMemoryFontRepository();
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executor = CreateExecutor(
            repository,
            refresh: async (_, _, cancellationToken) =>
            {
                refreshStarted.SetResult();
                await releaseRefresh.Task.WaitAsync(cancellationToken);
                return new FontInventoryScanResult(1, true, FontAnalysisInputSnapshot.Empty, []);
            });
        Task<FontTaskExecutionResult> analysis = executor.RefreshInventoryAndAnalyzeAsync(
            new InlineProgress(_ => { }),
            CancellationToken.None);
        await refreshStarted.Task;

        FontTaskExecutionResult result = await executor.QuarantineDuplicatesAsync(new InlineProgress(_ => { }), CancellationToken.None);

        releaseRefresh.SetResult();
        await analysis;
        Assert.Equal("task-active", result.Outcome);
    }

    [Fact]
    public async Task RefreshAndAnalyzeAsync_ShouldSkipWhileFileMaintenanceIsActive()
    {
        var repository = new InMemoryFontRepository();
        var purgeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePurge = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int refreshCalls = 0;
        var executor = CreateExecutor(
            repository,
            refresh: (_, _, _) =>
            {
                refreshCalls++;
                return Task.FromResult(new FontInventoryScanResult(1, true, FontAnalysisInputSnapshot.Empty, []));
            },
            purge: async cancellationToken =>
            {
                purgeStarted.SetResult();
                await releasePurge.Task.WaitAsync(cancellationToken);
                return 0;
            });
        Task<FontTaskExecutionResult> purge = executor.PurgeExpiredQuarantineAsync(new InlineProgress(_ => { }), CancellationToken.None);
        await purgeStarted.Task;

        FontTaskExecutionResult result = await executor.RefreshInventoryAndAnalyzeAsync(
            new InlineProgress(_ => { }),
            CancellationToken.None);

        releasePurge.SetResult();
        await purge;
        Assert.Equal("task-active", result.Outcome);
        Assert.Equal(0, refreshCalls);
    }

    [Fact]
    public async Task QuarantineDuplicatesAsync_ShouldSynthesizeAndExecuteOneCurrentRequest()
    {
        var repository = new InMemoryFontRepository();
        long generationId = await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await repository.WriteInventoryAsync(generationId, [File(10, generationId)], CancellationToken.None);
        AnalysisBatch analysis = await repository.PublishAnalysisAsync(
            Analysis(generationId) with
            {
                Groups =
                [
                    new DuplicateGroupRecord
                    {
                        RetainedFaceId = 100,
                        ReclaimableBytes = 20,
                        EvidenceJson = "{}",
                        Members =
                        [
                            new DuplicateGroupMemberRecord
                            {
                                FaceId = 100,
                                FileId = 1,
                                Decision = DuplicateGroupMemberDecision.Retained,
                                ScoreJson = "{}",
                                Reason = "retained"
                            },
                            new DuplicateGroupMemberRecord
                            {
                                FaceId = 101,
                                FileId = 10,
                                Decision = DuplicateGroupMemberDecision.QuarantineCandidate,
                                ScoreJson = "{}",
                                Reason = "duplicate"
                            }
                        ]
                    }
                ]
            },
            CancellationToken.None);
        OperationRequest? observed = null;
        var executor = CreateExecutor(
            repository,
            quarantine: (request, _, _) =>
            {
                observed = request;
                return Task.FromResult(Maintenance(44, request.Id));
            });

        FontTaskExecutionResult result = await executor.QuarantineDuplicatesAsync(new InlineProgress(_ => { }), CancellationToken.None);

        Assert.NotNull(observed);
        Assert.Equal(analysis.Id, observed.AnalysisBatchId);
        Assert.Equal(FontOperationKind.Quarantine, observed.Kind);
        Assert.Equal(44, result.BatchId);
        Assert.Null(await repository.ClaimNextOperationAsync(FontOperationKind.Quarantine, CancellationToken.None));
    }

    [Fact]
    public async Task RestoreAndNormalize_ShouldConsumeOnlyExplicitRequests()
    {
        var repository = new InMemoryFontRepository();
        var requestStore = new FontOperationRequestStore(repository, TimeProvider.System);
        int restoreCalls = 0;
        int normalizeCalls = 0;
        var normalizer = new RecordingFilenameNormalizer(request =>
        {
            normalizeCalls++;
            return Maintenance(52, request.Id, FontOperationKind.NormalizeNames);
        });
        var executor = CreateExecutor(
            repository,
            requestStore,
            restore: (request, _) =>
            {
                restoreCalls++;
                return Task.FromResult(Maintenance(51, request.Id, FontOperationKind.Restore));
            },
            filenameNormalizer: normalizer);

        Assert.Equal("request-missing", (await executor.RestoreQuarantinedFontsAsync(CancellationToken.None)).Outcome);
        Assert.Equal("rename-candidates-empty", (await executor.NormalizeFilenamesAsync(new InlineProgress(_ => { }), CancellationToken.None)).Outcome);
        Assert.Equal(0, restoreCalls);
        Assert.Equal(0, normalizeCalls);

        await requestStore.CreateAsync(FontOperationKind.Restore, 5, [10], "restore-1", CancellationToken.None);
        await repository.CreateOperationRequestAsync(new OperationRequest { Kind = FontOperationKind.NormalizeNames, IdempotencyKey = "rename-1", RequestJson = "{}" }, CancellationToken.None);

        Assert.Equal(51, (await executor.RestoreQuarantinedFontsAsync(CancellationToken.None)).BatchId);
        Assert.Equal(52, (await executor.NormalizeFilenamesAsync(new InlineProgress(_ => { }), CancellationToken.None)).BatchId);
        Assert.Equal(1, restoreCalls);
        Assert.Equal(1, normalizeCalls);
        Assert.Null(await repository.ClaimNextOperationAsync(FontOperationKind.Restore, CancellationToken.None));
        Assert.Null(await repository.ClaimNextOperationAsync(FontOperationKind.NormalizeNames, CancellationToken.None));
    }

    [Fact]
    public async Task PurgeExpiredQuarantineAsync_ShouldDelegateFixedPolicyVerification()
    {
        var repository = new InMemoryFontRepository();
        int calls = 0;
        var executor = CreateExecutor(
            repository,
            purge: _ =>
            {
                calls++;
                return Task.FromResult(3);
            });

        FontTaskExecutionResult result = await executor.PurgeExpiredQuarantineAsync(new InlineProgress(_ => { }), CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Equal("purged:3", result.Outcome);
    }

    [Fact]
    public async Task PurgeExpiredQuarantineAsync_ShouldClaimAndCompleteAnExplicitRequest()
    {
        var repository = new InMemoryFontRepository();
        OperationRequest request = await CreateTaskRequestAsync(repository, FontOperationKind.Purge, "explicit-purge");
        int calls = 0;
        var executor = CreateExecutor(repository, purge: _ =>
        {
            calls++;
            return Task.FromResult(3);
        });

        FontTaskExecutionResult result = await executor.PurgeExpiredQuarantineAsync(new InlineProgress(_ => { }), CancellationToken.None);

        OperationRequest persisted = Assert.Single(repository.OperationRequests);
        Assert.Equal(1, calls);
        Assert.Equal(request.Id, result.OperationRequestId);
        Assert.Null(result.BatchId);
        Assert.Equal(OperationRequestStatus.Complete, persisted.Status);
    }

    [Fact]
    public async Task PurgeExpiredQuarantineAsync_ShouldSynthesizeClaimAndCompleteOneRequest()
    {
        var repository = new InMemoryFontRepository();
        int calls = 0;
        var executor = CreateExecutor(repository, purge: _ =>
        {
            calls++;
            return Task.FromResult(3);
        });

        FontTaskExecutionResult result = await executor.PurgeExpiredQuarantineAsync(new InlineProgress(_ => { }), CancellationToken.None);

        OperationRequest persisted = Assert.Single(repository.OperationRequests);
        Assert.Equal(1, calls);
        Assert.Equal(FontOperationKind.Purge, persisted.Kind);
        Assert.Equal(persisted.Id, result.OperationRequestId);
        Assert.Equal(OperationRequestStatus.Complete, persisted.Status);
    }

    [Fact]
    public async Task PurgeExpiredQuarantineAsync_ShouldCancelTheClaimedRequest()
    {
        var repository = new InMemoryFontRepository();
        await CreateTaskRequestAsync(repository, FontOperationKind.Purge, "cancelled-purge");
        using var cancellation = new CancellationTokenSource();
        var executor = CreateExecutor(repository, purge: _ =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<int>(cancellation.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.PurgeExpiredQuarantineAsync(new InlineProgress(_ => { }), cancellation.Token));

        OperationRequest persisted = Assert.Single(repository.OperationRequests);
        Assert.Equal(OperationRequestStatus.Cancelled, persisted.Status);
    }

    [Fact]
    public async Task PurgeExpiredQuarantineAsync_ShouldFailTheClaimedRequestAndRetainItsIdentifier()
    {
        var repository = new InMemoryFontRepository();
        OperationRequest request = await CreateTaskRequestAsync(repository, FontOperationKind.Purge, "failing-purge");
        var executor = CreateExecutor(
            repository,
            purge: _ => Task.FromException<int>(new InvalidOperationException("purge failure")));

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.PurgeExpiredQuarantineAsync(new InlineProgress(_ => { }), CancellationToken.None));

        OperationRequest persisted = Assert.Single(repository.OperationRequests);
        Assert.Equal(request.Id, Assert.IsType<long>(exception.Data[FontScheduledTaskExecution.OperationRequestIdDataKey]));
        Assert.Equal(OperationRequestStatus.Failed, persisted.Status);
        Assert.Equal("purge failure", persisted.Error);
    }

    [Fact]
    public async Task NormalizeFilenamesAsync_ShouldCreateClaimAndExecuteTheLatestValidPlanRequest()
    {
        var repository = new InMemoryFontRepository();
        int normalizeCalls = 0;
        var normalizer = new RecordingFilenameNormalizer(
            execute: request =>
            {
                normalizeCalls++;
                return Maintenance(52, request.Id, FontOperationKind.NormalizeNames);
            },
            createLatestPlanRequest: async cancellationToken =>
                (OperationRequest?)await repository.CreateOperationRequestAsync(
                    new OperationRequest
                    {
                        Kind = FontOperationKind.NormalizeNames,
                        Status = OperationRequestStatus.Pending,
                        IdempotencyKey = "latest-rename-plan",
                        RequestJson = "{}",
                        CreatedUtc = DateTimeOffset.UtcNow
                    },
                    cancellationToken));
        var executor = CreateExecutor(repository, filenameNormalizer: normalizer);

        FontTaskExecutionResult result = await executor.NormalizeFilenamesAsync(new InlineProgress(_ => { }), CancellationToken.None);

        OperationRequest persisted = Assert.Single(repository.OperationRequests);
        Assert.Equal(1, normalizer.LatestPlanRequestCalls);
        Assert.Equal(1, normalizeCalls);
        Assert.Equal(persisted.Id, result.OperationRequestId);
        Assert.Equal(52, result.BatchId);
        Assert.Equal(OperationRequestStatus.Complete, persisted.Status);
    }

    [Fact]
    public async Task NormalizeFilenamesAsync_ShouldReturnNoPlanWhenTheLatestPlanSeamFindsNone()
    {
        var repository = new InMemoryFontRepository();
        var normalizer = new RecordingFilenameNormalizer(createLatestPlanRequest: _ => Task.FromResult<OperationRequest?>(null));
        var executor = CreateExecutor(repository, filenameNormalizer: normalizer);

        FontTaskExecutionResult result = await executor.NormalizeFilenamesAsync(new InlineProgress(_ => { }), CancellationToken.None);

        Assert.Equal("rename-candidates-empty", result.Outcome);
        Assert.Equal(1, normalizer.LatestPlanRequestCalls);
        Assert.Empty(repository.OperationRequests);
    }

    [Fact]
    public async Task NormalizeFilenamesAsync_ShouldConsumeRollbackBeforeSynthesizingAPlan()
    {
        var repository = new InMemoryFontRepository();
        OperationRequest request = await CreateTaskRequestAsync(repository, FontOperationKind.RollbackNames, "rollback-pending");
        var normalizer = new RecordingFilenameNormalizer();
        using var executor = CreateExecutor(repository, filenameNormalizer: normalizer);
        await executor.NormalizeFilenamesAsync(new InlineProgress(_ => { }), CancellationToken.None);
        Assert.Equal(OperationRequestStatus.Complete, (await repository.GetOperationRequestAsync(request.Id, CancellationToken.None))!.Status);
        Assert.Equal(0, normalizer.LatestPlanRequestCalls);
    }

    [Fact]
    public async Task NormalizeFilenamesAsync_ShouldSignalPendingRequestsOnlyAfterTaskRelease()
    {
        var repository = new InMemoryFontRepository();
        await CreateTaskRequestAsync(repository, FontOperationKind.NormalizeNames, "rename-with-followup");
        int signals = 0;
        FontMaintenanceTaskExecutor? executor = null;
        executor = CreateExecutor(repository, signalPendingRequests: () =>
        {
            Assert.False(executor!.IsBusy);
            signals++;
        });
        using (executor)
        {
            await executor.NormalizeFilenamesAsync(new InlineProgress(_ => { }), CancellationToken.None);
            Assert.Equal(1, signals);
        }
    }

    [Fact]
    public async Task PruneHistoryAsync_SkipsActiveTaskAndThenRemovesObsoleteInventory()
    {
        var repository = new InMemoryFontRepository();
        long obsolete = await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await repository.WriteInventoryAsync(obsolete, [File(1, obsolete)], CancellationToken.None);
        await repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = obsolete, Status = AnalysisBatchStatus.Complete, SummaryJson = "{\"faceScores\":[]}" }, CancellationToken.None);
        long published = await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await repository.WriteInventoryAsync(published, [File(2, published)], CancellationToken.None);
        await repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = published, Status = AnalysisBatchStatus.Complete, SummaryJson = "{\"faceScores\":[]}" }, CancellationToken.None);
        var refreshStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource<FontInventoryScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var executor = CreateExecutor(repository, refresh: (_, _, _) =>
        {
            refreshStarted.SetResult();
            return releaseRefresh.Task;
        });

        Task<FontTaskExecutionResult> running = executor.RefreshInventoryAndAnalyzeAsync(new InlineProgress(_ => { }), CancellationToken.None);
        await refreshStarted.Task;
        FontHistoryPruneResult skipped = await executor.PruneHistoryAsync(1, new InlineProgress(_ => { }), CancellationToken.None);
        Assert.True(skipped.Skipped);
        Assert.Single(await repository.LoadInventoryAsync(obsolete, CancellationToken.None));

        releaseRefresh.SetResult(new FontInventoryScanResult(published, true, FontAnalysisInputSnapshot.Empty, []));
        await running;
        FontHistoryPruneResult removed = await executor.PruneHistoryAsync(1, new InlineProgress(_ => { }), CancellationToken.None);
        Assert.False(removed.Skipped);
        Assert.Equal(1, removed.DeletedGenerations);
        Assert.Empty(await repository.LoadInventoryAsync(obsolete, CancellationToken.None));
        Assert.Single(await repository.LoadInventoryAsync(published, CancellationToken.None));
    }

    private static FontMaintenanceTaskExecutor CreateExecutor(
        InMemoryFontRepository repository,
        FontOperationRequestStore? requestStore = null,
        Func<IProgress<double>?, bool, CancellationToken, Task<FontInventoryScanResult>>? refresh = null,
        Func<long, CancellationToken, Task<AnalysisBatch>>? analyze = null,
        Func<long, IProgress<double>?, CancellationToken, Task<AnalysisBatch>>? analyzeWithProgress = null,
        Func<OperationRequest, IProgress<double>?, CancellationToken, Task<MaintenanceBatch>>? quarantine = null,
        Func<OperationRequest, CancellationToken, Task<MaintenanceBatch>>? restore = null,
        Func<CancellationToken, Task<int>>? purge = null,
        IFontFilenameNormalizationExecutor? filenameNormalizer = null,
        Action? signalPendingRequests = null,
        Func<AnalysisBatch, CancellationToken, Task<AnalysisBatch>>? publish = null,
        Func<FontInventoryScanResult, CancellationToken, Task>? validate = null,
        Func<AnalysisBatch, CancellationToken, Task<FontRenameCandidatePreparation>>? prepare = null,
        Func<long, CancellationToken, Task>? repairCandidates = null,
        Func<AnalysisBatch, FontRenameCandidatePreparation?, CancellationToken, Task<AnalysisBatch>>? publishPrepared = null)
    {
        return new FontMaintenanceTaskExecutor(
            repository,
            requestStore ?? new FontOperationRequestStore(repository, TimeProvider.System),
            TimeProvider.System,
            filenameNormalizer ?? new RecordingFilenameNormalizer(),
            refresh ?? ((_, _, _) => Task.FromResult(new FontInventoryScanResult(1L, true, FontAnalysisInputSnapshot.Empty, []))),
            analyzeWithProgress ?? ((generationId, _, token) => (analyze ?? ((id, _) => Task.FromResult(Analysis(id, 1))))(generationId, token)),
            quarantine ?? ((request, _, _) => Task.FromResult(Maintenance(1, request.Id))),
            restore ?? ((request, _) => Task.FromResult(Maintenance(1, request.Id, FontOperationKind.Restore))),
            (_, token) => (purge ?? (_ => Task.FromResult(0)))(token),
            publishPrepared ?? ((batch, _, token) => (publish ?? ((b, _) => Task.FromResult(b with { Id = b.Id == 0 ? 1 : b.Id })))(batch, token)),
            signalPendingRequests, validate is null ? null : (scan, _, token) => validate(scan, token), prepare, repairCandidates);
    }

    private static AnalysisBatch Analysis(long generationId, long id = 0)
    {
        return new AnalysisBatch
        {
            Id = id,
            GenerationId = generationId,
            Status = AnalysisBatchStatus.Complete,
            EvidenceVersion = 1,
            StartedUtc = DateTimeOffset.UtcNow,
            CompletedUtc = DateTimeOffset.UtcNow,
            SummaryJson = "{}"
        };
    }

    private static FontFileRecord File(long fileId, long generationId)
    {
        return new FontFileRecord
        {
            Id = fileId,
            GenerationId = generationId,
            Path = $"font-{fileId}.ttf",
            NormalizedPath = $"FONT-{fileId}.TTF",
            PhysicalIdentity = $"file-{fileId}",
            Sha256 = new byte[] { 1, 2, 3 },
            Health = FontFileHealth.Healthy,
            Faces =
            [
                new FontFaceRecord
                {
                    Id = fileId * 10,
                    FaceIndex = 0,
                    StrictFingerprint = new byte[] { 4, 5, 6 },
                    FamilyName = "Example",
                    StyleName = "Regular",
                    Names = ["Example"],
                    Technology = "TrueType"
                }
            ]
        };
    }

    private static MaintenanceBatch Maintenance(
        long id,
        long requestId = 1,
        FontOperationKind kind = FontOperationKind.Quarantine)
    {
        return new MaintenanceBatch
        {
            Id = id,
            OperationRequestId = requestId,
            Kind = kind,
            Status = MaintenanceBatchStatus.Complete,
            StartedUtc = DateTimeOffset.UtcNow,
            CompletedUtc = DateTimeOffset.UtcNow,
            SummaryJson = "{}"
        };
    }

    private static Task<OperationRequest> CreateTaskRequestAsync(
        InMemoryFontRepository repository,
        FontOperationKind kind,
        string idempotencyKey)
    {
        return repository.CreateOperationRequestAsync(
            new OperationRequest
            {
                Kind = kind,
                Status = OperationRequestStatus.Pending,
                IdempotencyKey = idempotencyKey,
                RequestJson = "{}",
                CreatedUtc = DateTimeOffset.UtcNow
            },
            CancellationToken.None);
    }

    private static void AssertDailyWithoutStartup(IScheduledTask task)
    {
        TaskTriggerInfo trigger = Assert.Single(task.GetDefaultTriggers());
        Assert.Equal(TaskTriggerInfoType.IntervalTrigger, trigger.Type);
        Assert.Equal(TimeSpan.FromHours(24).Ticks, trigger.IntervalTicks);
    }

    private static void AssertSingleton<TService>(IEnumerable<ServiceDescriptor> services)
    {
        ServiceDescriptor descriptor = Assert.Single(services, item => item.ServiceType == typeof(TService));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }
}

internal sealed class RecordingTaskExecutor : IFontMaintenanceTaskExecutor
{
    public Task<FontTaskExecutionResult> ExecutePendingOperationAsync(FontOperationKind kind, IProgress<double> progress, CancellationToken cancellationToken)
        => Task.FromResult(new FontTaskExecutionResult(null, "request-missing"));

    public bool IsBusy => false;

    public int RefreshAndAnalyzeCalls { get; private set; }

    public int QuarantineCalls { get; private set; }

    public int RestoreCalls { get; private set; }

    public int PurgeCalls { get; private set; }

    public int NormalizeCalls { get; private set; }

    public Task<FontHistoryPruneResult> PruneHistoryAsync(int retentionCount, IProgress<double> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new FontHistoryPruneResult(0, 0, false));
    }

    public Task<FontTaskExecutionResult> RefreshInventoryAndAnalyzeAsync(
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RefreshAndAnalyzeCalls++;
        return Task.FromResult(new FontTaskExecutionResult(1, "analyzed"));
    }

    public Task<FontTaskExecutionResult> QuarantineDuplicatesAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        QuarantineCalls++;
        return Task.FromResult(new FontTaskExecutionResult(2, "quarantined"));
    }

    public Task<FontTaskExecutionResult> RestoreQuarantinedFontsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RestoreCalls++;
        return Task.FromResult(new FontTaskExecutionResult(3, "restored"));
    }

    public Task<FontTaskExecutionResult> PurgeExpiredQuarantineAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PurgeCalls++;
        return Task.FromResult(new FontTaskExecutionResult(null, "purged:0"));
    }

    public Task<FontTaskExecutionResult> NormalizeFilenamesAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NormalizeCalls++;
        return Task.FromResult(new FontTaskExecutionResult(4, "normalized"));
    }
}

internal sealed class RecordingFilenameNormalizer : IFontFilenameNormalizationExecutor
{
    public Action? Recovery { get; set; }

    public Task ReconcileAsync(CancellationToken cancellationToken)
    {
        Recovery?.Invoke();
        return Task.CompletedTask;
    }

    public Task<MaintenanceBatch> RollbackAsync(OperationRequest request, CancellationToken cancellationToken, IProgress<double>? progress = null) => ExecuteAsync(request, cancellationToken);

    private readonly Func<OperationRequest, MaintenanceBatch>? _execute;
    private readonly Func<CancellationToken, Task<OperationRequest?>>? _createLatestPlanRequest;

    public RecordingFilenameNormalizer(
        Func<OperationRequest, MaintenanceBatch>? execute = null,
        Func<CancellationToken, Task<OperationRequest?>>? createLatestPlanRequest = null)
    {
        _execute = execute;
        _createLatestPlanRequest = createLatestPlanRequest;
    }

    public int ExecuteCalls { get; private set; }

    public int LatestPlanRequestCalls { get; private set; }

    public Task<OperationRequest?> CreateCurrentRequestAsync(CancellationToken cancellationToken)
    {
        LatestPlanRequestCalls++;
        return _createLatestPlanRequest?.Invoke(cancellationToken) ?? Task.FromResult<OperationRequest?>(null);
    }

    public Func<OperationRequest, Task<MaintenanceBatch>>? Execute { get; set; }

    public Task<MaintenanceBatch> ExecuteAsync(OperationRequest request, CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        if (Execute is not null) return Execute(request);
        cancellationToken.ThrowIfCancellationRequested();
        ExecuteCalls++;
        return Task.FromResult(_execute?.Invoke(request) ?? FontMaintenanceTaskTestsAccessor.Maintenance(request.Id));
    }
}

internal static class FontMaintenanceTaskTestsAccessor
{
    public static MaintenanceBatch Maintenance(long requestId)
    {
        return new MaintenanceBatch
        {
            Id = requestId,
            OperationRequestId = requestId,
            Kind = FontOperationKind.NormalizeNames,
            Status = MaintenanceBatchStatus.Complete,
            StartedUtc = DateTimeOffset.UtcNow,
            CompletedUtc = DateTimeOffset.UtcNow,
            SummaryJson = "{}"
        };
    }
}

internal sealed class InlineProgress(Action<double> report) : IProgress<double>
{
    public void Report(double value) => report(value);
}
