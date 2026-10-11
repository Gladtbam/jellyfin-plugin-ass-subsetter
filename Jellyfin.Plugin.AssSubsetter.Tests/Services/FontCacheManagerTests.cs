using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;
using Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;
using Jellyfin.Plugin.AssSubsetter.Models;
using Jellyfin.Plugin.AssSubsetter.Services;
using Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Maintenance;
using Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Services;

public sealed class FontCacheManagerTests
{
    [Fact]
    public async Task CandidateWriteFailureKeepsOldRuntimeAndPublication()
    {
        await using var fixture = await SqliteRenameCandidateTests.Fixture.CreateAsync();
        string root = Path.GetDirectoryName(fixture.Items[0].File.Path)!;
        long oldGeneration = await fixture.Repository.BeginGenerationAsync(FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await fixture.Repository.WriteInventoryAsync(oldGeneration, [RuntimeFile("OldFont", Path.Combine(root, "old.ttf")) with { GenerationId = oldGeneration }], CancellationToken.None);
        var oldFile = Assert.Single(await fixture.Repository.LoadInventoryAsync(oldGeneration, CancellationToken.None));
        var oldPrepared = new FontRenameCandidatePreparation(fixture.State with { GenerationId = oldGeneration, AnalysisBatchId = 0 }, [new FontRenameCandidate { File = oldFile, SourceRoot = root, NewPath = Path.Combine(root, "OldFont.ttf") }]);
        var oldAnalysis = await fixture.Repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = oldGeneration, Status = AnalysisBatchStatus.Complete, EvidenceVersion = 1, SummaryJson = "{\"faceScores\":[]}" }, CancellationToken.None, oldPrepared);
        using var manager = FontCacheManagerFixture.CreateManager(fixture.Repository);
        await manager.LoadPublishedSnapshotAsync(restoreFencesAsync: null, CancellationToken.None);
        Assert.Equal(oldFile.Path, (await FontCacheManagerFixture.LookupAsync(manager, new FontDescriptor("OldFont", null, false, false)))?.Path);
        long next = await fixture.Repository.BeginGenerationAsync(FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await fixture.Repository.WriteInventoryAsync(next, [RuntimeFile("NewFont", Path.Combine(root, "new.ttf")) with { GenerationId = next }], CancellationToken.None);
        var nextFile = Assert.Single(await fixture.Repository.LoadInventoryAsync(next, CancellationToken.None));
        var nextPrepared = new FontRenameCandidatePreparation(fixture.State with { GenerationId = next, AnalysisBatchId = 0 }, [new FontRenameCandidate { File = nextFile, SourceRoot = root, NewPath = Path.Combine(root, "NewFont.ttf") }]);
        await fixture.ExecuteAsync("CREATE TRIGGER fail_publication_candidate BEFORE INSERT ON rename_candidate BEGIN SELECT RAISE(ABORT, 'test candidate publication failure'); END;");
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => manager.PublishAnalyzedGenerationAsync(new AnalysisBatch { GenerationId = next, Status = AnalysisBatchStatus.Complete, EvidenceVersion = 1, SummaryJson = "{\"faceScores\":[]}" }, CancellationToken.None, nextPrepared));
        var current = await fixture.Repository.LoadRenameCandidatesAsync(null, fixture.Requirements, CancellationToken.None);
        Assert.Equal(oldGeneration, current.Context.Publication!.GenerationId);
        Assert.Equal(oldAnalysis.Id, current.Context.Publication.AnalysisBatchId);
        Assert.Equal(oldPrepared.Items[0].NewPath, Assert.Single(current.Items).NewPath);
        Assert.Equal(oldFile.Path, (await FontCacheManagerFixture.LookupAsync(manager, new FontDescriptor("OldFont", null, false, false)))?.Path);
        Assert.Null(await FontCacheManagerFixture.LookupAsync(manager, new FontDescriptor("NewFont", null, false, false)));
    }

    [Fact]
    public async Task StagedGeneration_ShouldNotReplaceDelayedStartupSnapshotUntilPublication()
    {
        var repository = new InMemoryFontRepository();
        await PublishAsync(repository, RuntimeFile("FakeFont", "/fonts/old.ttf"));
        using FontCacheManager manager = CreateManager(repository);
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.RuntimeSnapshotReadDelay = token =>
        {
            repository.RuntimeSnapshotReadDelay = null;
            readStarted.SetResult();
            return releaseRead.Task.WaitAsync(token);
        };
        Task startup = manager.LoadPublishedSnapshotAsync(restoreFencesAsync: null, TestContext.Current.CancellationToken);
        await readStarted.Task;
        Task<FontInventoryScanResult> scan = manager.StageInventoryAsync(cancellationToken: TestContext.Current.CancellationToken);

        long stagedGeneration = (await scan).GenerationId;
        Assert.Single(repository.PublishedFiles);
        releaseRead.SetResult();
        await startup;
        Assert.Equal("/fonts/old.ttf", (await FontCacheManagerFixture.LookupAsync(manager, new FontDescriptor("FakeFont", null, false, false)))?.Path);
        AnalysisBatch analysis = new AnalysisBatch { GenerationId = stagedGeneration, Status = AnalysisBatchStatus.Complete, SummaryJson = "{}" };
        await manager.PublishAnalyzedGenerationAsync(analysis, TestContext.Current.CancellationToken);
        Assert.Empty(repository.PublishedFiles);
        await using FontFileLease? lease = await manager.AcquireFontAsync(
            new FontDescriptor("FakeFont", null, false, false), Array.Empty<uint>(), TestContext.Current.CancellationToken);
        Assert.Null(lease);
    }

    [Fact]
    public async Task PublicationWithoutCompletedAnalysis_ShouldPreserveOldRuntimeAndDatabaseGeneration()
    {
        var repository = new InMemoryFontRepository();
        await PublishAsync(repository, RuntimeFile("FakeFont", "/fonts/old.ttf"));
        using FontCacheManager manager = CreateManager(repository);
        await manager.LoadPublishedSnapshotAsync(restoreFencesAsync: null, TestContext.Current.CancellationToken);
        long staged = (await manager.StageInventoryAsync(cancellationToken: TestContext.Current.CancellationToken)).GenerationId;

        await Assert.ThrowsAsync<InvalidOperationException>(() => manager.PublishAnalyzedGenerationAsync(new AnalysisBatch { GenerationId = staged, Status = AnalysisBatchStatus.Running, SummaryJson = "{}" }, TestContext.Current.CancellationToken));

        Assert.Equal("/fonts/old.ttf", Assert.Single(repository.PublishedFiles).Path);
        Assert.Equal("/fonts/old.ttf", (await FontCacheManagerFixture.LookupAsync(manager, new FontDescriptor("FakeFont", null, false, false)))?.Path);
    }

    [Fact]
    public async Task AnalyzedPublication_ShouldClearRenameFencesButPreserveQuarantineFences()
    {
        var repository = new InMemoryFontRepository();
        await PublishAsync(repository, RuntimeFile("FakeFont", "/fonts/old.ttf"));
        var coordinator = new FontMaintenanceCoordinator();
        using var manager = FontCacheManagerFixture.CreateManager(repository, coordinator);
        await manager.LoadPublishedSnapshotAsync(restoreFencesAsync: null, TestContext.Current.CancellationToken);
        coordinator.SetRenamePathAvailable("/fonts/old.ttf", false);
        coordinator.SetPathAvailable("/fonts/quarantined.ttf", false);
        Assert.Null(await FontCacheManagerFixture.LookupAsync(manager, new FontDescriptor("FakeFont", null, false, false)));

        long generation = await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], TestContext.Current.CancellationToken);
        await repository.WriteInventoryAsync(generation, [RuntimeFile("FakeFont", "/fonts/new.ttf") with { GenerationId = generation }], TestContext.Current.CancellationToken);
        AnalysisBatch analysis = new AnalysisBatch { GenerationId = generation, Status = AnalysisBatchStatus.Complete, SummaryJson = "{}" };
        await manager.PublishAnalyzedGenerationAsync(analysis, TestContext.Current.CancellationToken);

        Assert.True(coordinator.IsPathAvailable("/fonts/old.ttf"));
        Assert.False(coordinator.IsPathAvailable("/fonts/quarantined.ttf"));
        Assert.Equal("/fonts/new.ttf", (await FontCacheManagerFixture.LookupAsync(manager, new FontDescriptor("FakeFont", null, false, false)))?.Path);
    }

    [Fact]
    public async Task StartupFenceRecovery_ShouldSharePublicationBoundaryWithAnalyzedGeneration()
    {
        var repository = new InMemoryFontRepository();
        await PublishAsync(repository, RuntimeFile("FakeFont", "/fonts/old.ttf"));
        long generation = await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], TestContext.Current.CancellationToken);
        await repository.WriteInventoryAsync(generation, [RuntimeFile("FakeFont", "/fonts/new.ttf") with { GenerationId = generation }], TestContext.Current.CancellationToken);
        AnalysisBatch analysis = new AnalysisBatch { GenerationId = generation, Status = AnalysisBatchStatus.Complete, SummaryJson = "{}" };
        var coordinator = new FontMaintenanceCoordinator();
        using var manager = FontCacheManagerFixture.CreateManager(repository, coordinator);
        var recoveryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRecovery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task startup = manager.LoadPublishedSnapshotAsync(async token =>
        {
            recoveryStarted.SetResult();
            await releaseRecovery.Task.WaitAsync(token);
            coordinator.SetRenamePathAvailable("/fonts/old.ttf", false);
        }, TestContext.Current.CancellationToken);
        await recoveryStarted.Task;
        Task publish = manager.PublishAnalyzedGenerationAsync(analysis, TestContext.Current.CancellationToken);
        Assert.False(publish.IsCompleted);

        releaseRecovery.SetResult();
        await Task.WhenAll(startup, publish);

        Assert.True(coordinator.IsPathAvailable("/fonts/old.ttf"));
        Assert.Equal("/fonts/new.ttf", (await FontCacheManagerFixture.LookupAsync(manager, new FontDescriptor("FakeFont", null, false, false)))?.Path);
    }

    [Fact]
    public async Task CanceledPublicationWait_ShouldRetainOldSnapshotAndAllowRetry()
    {
        var repository = new InMemoryFontRepository();
        await PublishAsync(repository, RuntimeFile("FakeFont", "/fonts/previous.ttf"));
        using FontCacheManager manager = CreateManager(repository);
        await manager.LoadPublishedSnapshotAsync(restoreFencesAsync: null, TestContext.Current.CancellationToken);
        long generationId = (await manager.StageInventoryAsync(cancellationToken: TestContext.Current.CancellationToken)).GenerationId;
        AnalysisBatch analysis = new AnalysisBatch { GenerationId = generationId, Status = AnalysisBatchStatus.Complete, SummaryJson = "{}" };
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.RuntimeSnapshotReadDelay = token =>
        {
            repository.RuntimeSnapshotReadDelay = null;
            readStarted.SetResult();
            return releaseRead.Task.WaitAsync(token);
        };
        Task owner = manager.LoadPublishedSnapshotAsync(restoreFencesAsync: null, TestContext.Current.CancellationToken);
        await readStarted.Task;
        using var cancellation = new CancellationTokenSource();
        Task waiter = manager.PublishAnalyzedGenerationAsync(analysis, cancellation.Token);
        Task? subsequentPublication = null;
        try
        {
            await AssertPreviousMemoryLookupAsync(manager, repository);
            Assert.False(waiter.IsCompleted);
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
            subsequentPublication = manager.PublishAnalyzedGenerationAsync(analysis, TestContext.Current.CancellationToken);
            Assert.False(subsequentPublication.IsCompleted);
            Assert.False(owner.IsCompleted);
            await AssertPreviousMemoryLookupAsync(manager, repository);
        }
        finally
        {
            releaseRead.TrySetResult();
            await owner;
            if (subsequentPublication is not null)
            {
                await subsequentPublication;
            }
        }

        Assert.Null(await FontCacheManagerFixture.LookupAsync(manager, new FontDescriptor("FakeFont", null, false, false)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InterruptedStagedRead_ShouldRetainPreviousMemoryAndAllowRetry(bool cancel)
    {
        var repository = new InMemoryFontRepository();
        await PublishAsync(repository, RuntimeFile("FakeFont", "/fonts/previous.ttf"));
        using FontCacheManager manager = CreateManager(repository);
        await manager.LoadPublishedSnapshotAsync(restoreFencesAsync: null, TestContext.Current.CancellationToken);
        long generationId = (await manager.StageInventoryAsync(cancellationToken: TestContext.Current.CancellationToken)).GenerationId;
        AnalysisBatch analysis = new AnalysisBatch { GenerationId = generationId, Status = AnalysisBatchStatus.Complete, SummaryJson = "{}" };
        using var cancellation = new CancellationTokenSource();
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.RuntimeSnapshotReadDelay = token =>
        {
            readStarted.SetResult();
            return cancel ? releaseRead.Task.WaitAsync(token) : Task.FromException(new IOException("Controlled snapshot read failure."));
        };
        Task interrupted = manager.PublishAnalyzedGenerationAsync(analysis, cancellation.Token);
        await readStarted.Task;
        await AssertPreviousMemoryLookupAsync(manager, repository);
        if (cancel)
        {
            await cancellation.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interrupted);
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() => interrupted);
        }

        await AssertPreviousMemoryLookupAsync(manager, repository);
        repository.RuntimeSnapshotReadDelay = null;
        await manager.PublishAnalyzedGenerationAsync(analysis, TestContext.Current.CancellationToken);
        Assert.Null(await FontCacheManagerFixture.LookupAsync(manager, new FontDescriptor("FakeFont", null, false, false)));
    }

    [Fact]
    public async Task FailedScanRefresh_ShouldReleasePublicationBoundaryAndRetainPreviousSnapshot()
    {
        var repository = new InMemoryFontRepository();
        await PublishAsync(repository, RuntimeFile("FakeFont", "/fonts/previous.ttf"));
        using FontCacheManager manager = CreateManager(repository);
        await manager.LoadPublishedSnapshotAsync(restoreFencesAsync: null, TestContext.Current.CancellationToken);
        repository.InitializationDelay = _ => Task.FromException(new IOException("Controlled scan initialization failure."));

        await Assert.ThrowsAsync<IOException>(() => manager.StageInventoryAsync(cancellationToken: TestContext.Current.CancellationToken));
        await AssertPreviousMemoryLookupAsync(manager, repository);
        repository.InitializationDelay = null;
        Task recovered = manager.LoadPublishedSnapshotAsync(restoreFencesAsync: null, TestContext.Current.CancellationToken);
        Assert.True(recovered.IsCompletedSuccessfully);
        await recovered;
        await AssertPreviousMemoryLookupAsync(manager, repository);
    }

    [Fact]
    public async Task ColdLookups_ShouldReturnNoMatchWithoutAccessingRepository()
    {
        var repository = new InMemoryFontRepository();
        await PublishAsync(repository, RuntimeFile("FakeFont", "/fonts/published.ttf"));
        using FontCacheManager manager = CreateManager(repository);
        var descriptor = new FontDescriptor("FakeFont", null, false, false);

        await using FontFileLease? lease = await manager.AcquireFontAsync(descriptor, Array.Empty<uint>(), TestContext.Current.CancellationToken);
        Assert.Null(lease);
        Assert.Equal(0, repository.InitializeCallCount);
        Assert.Equal(0, repository.RuntimeSnapshotLoadCallCount);
    }

    [Fact]
    public async Task StartupService_ShouldLoadPublishedSnapshotBeforeRuntimeLookup()
    {
        var repository = new InMemoryFontRepository();
        await PublishAsync(repository, RuntimeFile("FakeFont", "/fonts/published.ttf"));
        var coordinator = new FontMaintenanceCoordinator();
        using FontCacheManager manager = FontCacheManagerFixture.CreateManager(repository, coordinator);
        IHostedService startup = new FontRuntimeSnapshotStartupService(manager, NullLogger<FontRuntimeSnapshotStartupService>.Instance, repository,
            new FileRenameManifestStore(Path.Combine(Path.GetTempPath(), "ass-startup-" + Guid.NewGuid().ToString("N"))),
            new InMemoryQuarantineManifestStore(), coordinator);

        await startup.StartAsync(TestContext.Current.CancellationToken);
        (string Path, int FaceIndex)? path = await FontCacheManagerFixture.LookupAsync(manager, new FontDescriptor("FakeFont", null, false, false));

        Assert.NotNull(path);
        Assert.Equal("/fonts/published.ttf", path.Value.Path);
        Assert.Equal(1, repository.InitializeCallCount);
        Assert.Equal(1, repository.RuntimeSnapshotLoadCallCount);
    }

    [Fact]
    public async Task ColdLookups_ShouldNotWaitForAnInProgressStartupLoad()
    {
        var loading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new InMemoryFontRepository { InitializationDelay = _ => loading.Task };
        var coordinator = new FontMaintenanceCoordinator();
        using FontCacheManager manager = FontCacheManagerFixture.CreateManager(repository, coordinator);
        var startup = new FontRuntimeSnapshotStartupService(manager, NullLogger<FontRuntimeSnapshotStartupService>.Instance, repository,
            new FileRenameManifestStore(Path.Combine(Path.GetTempPath(), "ass-startup-" + Guid.NewGuid().ToString("N"))),
            new InMemoryQuarantineManifestStore(), coordinator);
        Task startupTask = startup.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var descriptor = new FontDescriptor("FakeFont", null, false, false);
            Task<FontFileLease?> lookup = manager.AcquireFontAsync(descriptor, Array.Empty<uint>(), TestContext.Current.CancellationToken);

            Assert.True(lookup.IsCompletedSuccessfully);
            Assert.Null(await lookup);
            Assert.False(startupTask.IsCompleted);
            Assert.Equal(1, repository.InitializeCallCount);
            Assert.Equal(0, repository.RuntimeSnapshotLoadCallCount);
        }
        finally
        {
            loading.TrySetResult();
            await startupTask;
        }
    }

    [Fact]
    public async Task AcquireFontAsync_ShouldUseLoadedMemoryIndex_WhenRepositoryGenerationChanges()
    {
        var repository = new InMemoryFontRepository();
        await PublishAsync(repository, RuntimeFile("FakeFont", "/fonts/first.ttf"));
        using FontCacheManager manager = CreateManager(repository);
        await manager.LoadPublishedSnapshotAsync(restoreFencesAsync: null, TestContext.Current.CancellationToken);
        await PublishAsync(repository, RuntimeFile("FakeFont", "/fonts/second.ttf"));

        await using FontFileLease? lease = await manager.AcquireFontAsync(
            new FontDescriptor("FakeFont", null, false, false),
            Array.Empty<uint>(),
            TestContext.Current.CancellationToken);

        Assert.NotNull(lease);
        Assert.Equal("/fonts/first.ttf", lease.Path);
        Assert.Equal(1, repository.InitializeCallCount);
        Assert.Equal(1, repository.RuntimeSnapshotLoadCallCount);
    }

    [Fact]
    public async Task AcquireFontAsync_ShouldChoosePublishedFaceWithRequiredCharacterCoverage()
    {
        var repository = new InMemoryFontRepository();
        FontFileRecord partial = RuntimeFile("Example", "/fonts/partial.ttf");
        FontFileRecord complete = RuntimeFile("Example", "/fonts/complete.ttf");
        await PublishAsync(
            repository,
            partial with { Faces = partial.Faces.Select(face => face with { UnicodeCoverage = [new UnicodeRange('A', 'A')], RetentionScore = 90 }).ToArray() },
            complete with { Faces = complete.Faces.Select(face => face with { UnicodeCoverage = [new UnicodeRange('A', 'B')], RetentionScore = 10 }).ToArray() });
        using FontCacheManager manager = CreateManager(repository);
        await manager.LoadPublishedSnapshotAsync(restoreFencesAsync: null, TestContext.Current.CancellationToken);

        await using FontFileLease? lease = await manager.AcquireFontAsync(
            new FontDescriptor("Example", 400, false, false),
            ['A', 'B'],
            TestContext.Current.CancellationToken);

        Assert.NotNull(lease);
        Assert.Equal("/fonts/complete.ttf", lease.Path);
        Assert.Equal(1, repository.RuntimeSnapshotLoadCallCount);
    }

    [Fact]
    public async Task AcquireFontAsync_ShouldUseBoundedLooseAliasMatch()
    {
        var repository = new InMemoryFontRepository();
        await PublishAsync(repository, RuntimeFile("Comic Sans MS", "/fonts/comic.ttf"));
        using FontCacheManager manager = CreateManager(repository);
        await manager.LoadPublishedSnapshotAsync(restoreFencesAsync: null, TestContext.Current.CancellationToken);

        (string Path, int FaceIndex)? result = await FontCacheManagerFixture.LookupAsync(manager, new FontDescriptor("Comic Sans", null, false, false));

        Assert.NotNull(result);
        Assert.Equal("/fonts/comic.ttf", result.Value.Path);
    }

    private static async Task AssertPreviousMemoryLookupAsync(FontCacheManager manager, InMemoryFontRepository repository)
    {
        int initialized = repository.InitializeCallCount;
        int loaded = repository.RuntimeSnapshotLoadCallCount;
        var descriptor = new FontDescriptor("FakeFont", null, false, false);
        Task<FontFileLease?> lookup = manager.AcquireFontAsync(descriptor, Array.Empty<uint>(), TestContext.Current.CancellationToken);
        Assert.True(lookup.IsCompletedSuccessfully);
        await using FontFileLease? lease = await lookup;
        Assert.NotNull(lease);
        Assert.Equal("/fonts/previous.ttf", lease.Path);
        Assert.Equal(initialized, repository.InitializeCallCount);
        Assert.Equal(loaded, repository.RuntimeSnapshotLoadCallCount);
    }

    private static FontCacheManager CreateManager(IFontRepository repository)
        => FontCacheManagerFixture.CreateManager(repository);

    private static async Task PublishAsync(InMemoryFontRepository repository, params FontFileRecord[] files)
    {
        long generationId = await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], TestContext.Current.CancellationToken);
        await repository.WriteInventoryAsync(generationId, files.Select(file => file with { GenerationId = generationId }).ToArray(), TestContext.Current.CancellationToken);
        await repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = generationId, Status = AnalysisBatchStatus.Complete, SummaryJson = "{\"faceScores\":[]}" }, TestContext.Current.CancellationToken);
    }

    private static FontFileRecord RuntimeFile(string familyName, string path)
    {
        return new FontFileRecord
        {
            Path = path,
            NormalizedPath = path,
            PhysicalIdentity = path,
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
}
