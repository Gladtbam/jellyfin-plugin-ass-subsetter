using System.Security.Cryptography;
using Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;
using Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;
using Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Persistence;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Maintenance;

public sealed class FontRenameCandidateServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "candidate-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryFontRepository _repository = new();
    private FontNamingRules _rules = new("{family}", "", false);
    private string? _protected;
    private Action? _duringProbe;

    [Fact]
    public async Task GetCurrentNeverCalculates()
    {
        var service = await PrepareAsync();
        await PersistCurrentAsync(service);
        _duringProbe = () => throw new Exception("Persisted reads must not calculate or probe files");
        int reads = _repository.InventoryLoadCallCount;
        Assert.Single((await service.GetCurrentAsync(CancellationToken.None)).Items);
        Assert.Equal(reads, _repository.InventoryLoadCallCount);
    }

    [Fact]
    public async Task SelectedAuthorizationDoesNotReadUnselectedFiles()
    {
        var service = await PrepareAsync(includeSecondFile: true);
        var saved = await PersistCurrentAsync(service);
        var selected = saved.Items[0];
        _duringProbe = () => { };
        await File.WriteAllBytesAsync(saved.Items[1].File.Path, [9, 9, 9], TestContext.Current.CancellationToken);
        var request = await SelectAsync(service, saved, "safe-selection");
        var payload = FontOperationRequestPayloadCodec.ParseRenameExecution(request.RequestJson);
        Assert.Equal(selected.File.PhysicalIdentity, Assert.Single(payload.Items).Evidence.PhysicalIdentity);
        Assert.Equal(selected.File.Sha256.ToArray(), payload.Items[0].Evidence.Sha256.ToArray());
        Assert.Equal(selected.NewPath, payload.Items[0].NewPath);
        Assert.Equal(saved.Rules, payload.Rules);
        Assert.NotNull(_repository.LastRenameSelection);
        Assert.Single(_repository.LastRenameSelection);
        Assert.Equal(selected.File.Id, _repository.LastRenameSelection[0]);
    }

    [Fact]
    public async Task SelectedEvidenceDriftRejectsNewRequest()
    {
        var service = await PrepareAsync();
        var saved = await PersistCurrentAsync(service);
        await File.WriteAllBytesAsync(saved.Items[0].File.Path, [9, 9, 9], TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => SelectAsync(service, saved, "changed-file"));
        Assert.Empty(_repository.OperationRequests);
    }

    [Fact]
    public async Task PreparationUsesStagedGenerationAndNewQuarantineDecisions()
    {
        var service = await PrepareAsync();
        long staged = await _repository.BeginGenerationAsync(Input(), [], CancellationToken.None);
        var files = (await _repository.LoadPublishedInventoryAsync(CancellationToken.None)).Select(file => file with { Id = 2, GenerationId = staged }).ToArray();
        await _repository.WriteInventoryAsync(staged, files, CancellationToken.None);
        var prepared = await service.PrepareForPublicationAsync(new AnalysisBatch
        {
            GenerationId = staged, Status = AnalysisBatchStatus.Complete, EvidenceVersion = FontDuplicateAnalyzer.CurrentEvidenceVersion, SummaryJson = "{}",
            Groups = [new DuplicateGroupRecord { EvidenceJson = "{}", Members = [new DuplicateGroupMemberRecord { FileId = 2, Decision = DuplicateGroupMemberDecision.QuarantineCandidate, ScoreJson = "{}", Reason = "duplicate" }] }]
        }, CancellationToken.None);
        Assert.Equal(staged, prepared.State.GenerationId);
        Assert.Equal(0, prepared.State.AnalysisBatchId);
        Assert.Equal(FontRenameCandidateGenerationStatus.Ready, prepared.State.Status);
        Assert.Equal(2, Assert.Single(prepared.Items).File.Id);
        Assert.Equal("quarantine-candidate", prepared.Items[0].SkipReason);
    }

    [Fact]
    public async Task InvalidRulesReturnFailedPreparation()
    {
        var service = await PrepareAsync();
        _rules = _rules with { FileNameTemplate = "{unknown}" };
        _duringProbe = () => throw new Exception("Invalid rules reached file checks");
        var analysis = (await _repository.GetLatestCompleteAnalysisAsync(CancellationToken.None))!;
        var prepared = await service.PrepareForPublicationAsync(analysis with { Id = 0 }, CancellationToken.None);
        Assert.Equal(FontRenameCandidateGenerationStatus.Failed, prepared.State.Status);
        Assert.NotNull(prepared.State.Error);
        Assert.Contains("Invalid", prepared.State.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(prepared.Items);
    }

    private async Task<FontRenameSnapshot> PersistCurrentAsync(FontRenameCandidateService service)
    {
        var snapshot = await service.GetCurrentAsync(CancellationToken.None);
        var requirements = new FontRenameCandidateReadRequirements(snapshot.RuleFingerprint, Input().PolicyFingerprint, Input().EvidenceFingerprint, FontDuplicateAnalyzer.CurrentEvidenceVersion, FontRenamePlanner.CurrentCandidateVersion);
        var state = new FontRenameCandidateState(snapshot.GenerationId, snapshot.AnalysisBatchId, snapshot.Rules, snapshot.RuleFingerprint, FontRenamePlanner.CurrentCandidateVersion, FontRenameCandidateGenerationStatus.Ready, DateTimeOffset.UnixEpoch, null, null);
        await _repository.SaveRenameCandidatesAsync(new(state, snapshot.Items), requirements, null, CancellationToken.None);
        return snapshot;
    }

    [Fact]
    public async Task CurrentReadDoesNotPersistRequestsOrBatches()
    {
        var service = await PrepareAsync();
        var first = await service.GetCurrentAsync(CancellationToken.None);
        var second = await service.GetCurrentAsync(CancellationToken.None);
        Assert.Equal(first.GenerationId, second.GenerationId);
        Assert.Single(first.Items);
        Assert.Empty(_repository.OperationRequests);
        Assert.Empty((await _repository.LoadMaintenanceBatchPageAsync(0, 10, null, CancellationToken.None)).Items);
    }

    [Fact]
    public async Task ExactSelectionStoresServerPathsAndRetryPreservesOriginalAuthorization()
    {
        var service = await PrepareAsync();
        var current = await service.GetCurrentAsync(CancellationToken.None);
        var request = await SelectAsync(service, current, " retry ");
        var payload = FontOperationRequestPayloadCodec.ParseRenameExecution(request.RequestJson);
        var item = Assert.Single(payload.Items);
        Assert.Equal(current.Items[0].File.Path, item.OldPath);
        Assert.Equal(Path.Combine(_root, "Example.ttf"), item.NewPath);
        _rules = _rules with { FolderTemplate = "changed" };
        var retry = await SelectAsync(service, current, "retry");
        Assert.Equal(request.Id, retry.Id);
        Assert.Equal(request.RequestJson, retry.RequestJson);
        await Assert.ThrowsAsync<InvalidOperationException>(() => SelectAsync(service, current, "other"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateRequestAsync(current.GenerationId, current.AnalysisBatchId, "different", [1], "retry", CancellationToken.None));
    }

    [Fact]
    public async Task RuleChangeDuringPreparationMarksCandidatesStale()
    {
        var service = await PrepareAsync();
        _duringProbe = () => _rules = _rules with { RemoveEmptyDirectories = true };
        var analysis = (await _repository.GetLatestCompleteAnalysisAsync(CancellationToken.None))!;
        Assert.Equal(FontRenameCandidateGenerationStatus.Stale, (await service.PrepareForPublicationAsync(analysis with { Id = 0 }, CancellationToken.None)).State.Status);
        Assert.Empty(_repository.OperationRequests);
    }

    [Fact]
    public async Task RuleDriftPreparationStillPublishesAnalysisAndQueuesFreshCandidates()
    {
        await using var fixture = await SqliteRenameCandidateTests.Fixture.CreateAsync();
        string root = Path.GetDirectoryName(fixture.Items[0].File.Path)!;
        var policy = new FontDirectoryPolicyResolver(root, null, null, false);
        var input = FontInventoryInputComparer.Capture([], policy, PhysicalFontAnalysisEvidenceSource.AnalysisCompatibilityKey);
        long generation = await fixture.Repository.BeginGenerationAsync(input, [], CancellationToken.None);
        await fixture.Repository.WriteInventoryAsync(generation, fixture.Items.Select(item => item.File with
        {
            Id = 0, GenerationId = generation, Faces = [FontNamingTestData.Face(names: [(1, "Example")])]
        }).ToArray(), CancellationToken.None);
        var analysis = new AnalysisBatch { GenerationId = generation, Status = AnalysisBatchStatus.Complete, EvidenceVersion = FontDuplicateAnalyzer.CurrentEvidenceVersion, SummaryJson = "{\"faceScores\":[]}" };
        var directories = new ReadOnlyDirectories(() => _duringProbe?.Invoke());
        var service = new FontRenameCandidateService(fixture.Repository, new FontRenamePlanner(directories), () => _rules, () => policy, TimeProvider.System, new PhysicalQuarantineFileSystem(), directories, new FontMaintenanceCoordinator());
        var unchanged = await service.PrepareForPublicationAsync(analysis, CancellationToken.None);
        Assert.Equal(FontRenameCandidateGenerationStatus.Ready, unchanged.State.Status);
        Assert.Contains(unchanged.Items, item => item.SkipReason is null);

        _duringProbe = () => _rules = _rules with { RemoveEmptyDirectories = true };
        var prepared = await service.PrepareForPublicationAsync(analysis, CancellationToken.None);
        Assert.Equal(FontRenameCandidateGenerationStatus.Stale, prepared.State.Status);
        var published = await fixture.Repository.PublishAnalysisAsync(analysis, CancellationToken.None, prepared);
        var loaded = await fixture.Repository.LoadRenameCandidatesAsync(null, service.ReadRequirements(), CancellationToken.None);
        Assert.Equal(generation, loaded.Context.Publication!.GenerationId);
        Assert.Equal(published.Id, loaded.Context.Publication.AnalysisBatchId);
        Assert.Equal(published.Id, loaded.Context.State!.AnalysisBatchId);
        Assert.Equal(FontRenameCandidateGenerationStatus.Stale, loaded.Context.State.Status);
        Assert.Empty(loaded.Items);
        Assert.Empty(prepared.Items);

        var queue = new FontRenameCandidateRefreshQueue(fixture.Repository, () => _rules, service.ReadRequirements, TimeProvider.System);
        Assert.NotNull(await queue.EnsureCurrentAsync(CancellationToken.None));
        Assert.Equal(loaded.Context.Publication, (await fixture.Repository.GetRenameCandidateContextAsync(service.ReadRequirements(), CancellationToken.None)).Publication);
    }

    [Fact]
    public async Task PolicyChangeRequiresFreshAnalysis()
    {
        var service = await PrepareAsync();
        _protected = _root;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetCurrentAsync(CancellationToken.None));
        Assert.Empty(_repository.OperationRequests);
    }

    [Fact]
    public async Task SupersededPublicationAndStaleAnalysisRejectNewAuthorization()
    {
        var service = await PrepareAsync();
        var snapshot = await service.GetCurrentAsync(CancellationToken.None);
        await _repository.MarkAnalysisStaleAsync(snapshot.GenerationId, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => SelectAsync(service, snapshot, "stale"));
        long generation = await _repository.BeginGenerationAsync(Input(), [], CancellationToken.None);
        await _repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = generation, Status = AnalysisBatchStatus.Complete, EvidenceVersion = FontDuplicateAnalyzer.CurrentEvidenceVersion, SummaryJson = "{}" }, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => SelectAsync(service, snapshot, "superseded"));
    }

    [Fact]
    public async Task OldEvidenceVersionCannotAuthorizeMoves()
    {
        var service = await PrepareAsync(evidenceVersion: 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetCurrentAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("{unknown}", 2)]
    [InlineData("{name}", 1)]
    public async Task InvalidRulesNeverReachPhysicalPlanning(string expression, int version)
    {
        var service = await PrepareAsync();
        _rules = new(expression, "", false, version);
        _duringProbe = () => throw new Exception("Invalid rules reached physical planning");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetCurrentAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(OperationRequestStatus.Complete)]
    [InlineData(OperationRequestStatus.Failed)]
    [InlineData(OperationRequestStatus.Cancelled)]
    public async Task ScheduledTerminalAttemptCanBeRetriedWithoutChangingOriginalAuthorization(OperationRequestStatus status)
    {
        var service = await PrepareAsync();
        var first = (await service.CreateScheduledRequestAsync(CancellationToken.None))!;
        await _repository.UpdateOperationAsync(first with { Status = status }, CancellationToken.None);
        var next = (await service.CreateScheduledRequestAsync(CancellationToken.None))!;
        Assert.NotEqual(first.Id, next.Id);
        Assert.Equal(OperationRequestStatus.Pending, next.Status);
        Assert.Equal(first.RequestJson, next.RequestJson);
        Assert.Null(await service.CreateScheduledRequestAsync(CancellationToken.None));
        var snapshot = await service.GetCurrentAsync(CancellationToken.None);
        var retransmission = await SelectAsync(service, snapshot, first.IdempotencyKey);
        Assert.Equal(first.Id, retransmission.Id);
        Assert.Equal(status, retransmission.Status);
        await _repository.UpdateOperationAsync(next with { Status = status }, CancellationToken.None);
        var third = (await service.CreateScheduledRequestAsync(CancellationToken.None))!;
        Assert.NotEqual(next.Id, third.Id);
        Assert.Equal(first.RequestJson, third.RequestJson);
        Assert.Equal(3, _repository.OperationRequests.Count);
    }

    [Fact]
    public async Task CancelledPreviewStopsBeforeProbingRemainingFiles()
    {
        var service = await PrepareAsync(includeSecondFile: true);
        using var cancellation = new CancellationTokenSource();
        int probes = 0;
        _duringProbe = () =>
        {
            probes++;
            cancellation.Cancel();
        };

        var analysis = (await _repository.GetLatestCompleteAnalysisAsync(CancellationToken.None))!;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PrepareForPublicationAsync(analysis with { Id = 0 }, cancellation.Token));

        Assert.Equal(1, probes);
        Assert.Empty(_repository.OperationRequests);
    }
    private FontDirectoryPolicyResolver Policy() => new(_root, _protected, null, false);
    private FontAnalysisInputSnapshot Input() => FontInventoryInputComparer.Capture([], Policy(), PhysicalFontAnalysisEvidenceSource.AnalysisCompatibilityKey);
    private static Task<OperationRequest> SelectAsync(FontRenameCandidateService service, FontRenameSnapshot current, string key)
        => service.CreateRequestAsync(current.GenerationId, current.AnalysisBatchId, current.RuleFingerprint, [1], key, CancellationToken.None);

    private async Task<FontRenameCandidateService> PrepareAsync(int evidenceVersion = FontDuplicateAnalyzer.CurrentEvidenceVersion, bool includeSecondFile = false)
    {
        Directory.CreateDirectory(_root);
        long generation = await _repository.BeginGenerationAsync(Input(), [], CancellationToken.None);
        var file = FontNamingTestData.File(Path.Combine(_root, "old.ttf"), FontNamingTestData.Face(names: [(1, "Example")]));
        var files = new List<FontFileRecord> { file with { GenerationId = generation } };
        if (includeSecondFile)
        {
            files.Add(FontNamingTestData.File(Path.Combine(_root, "other.ttf"), FontNamingTestData.Face(names: [(1, "Other")])) with { Id = 2, GenerationId = generation });
        }

        for (int index = 0; index < files.Count; index++)
        {
            var sourceFile = files[index];
            byte[] bytes = [(byte)index, 1, 2, 3];
            await File.WriteAllBytesAsync(sourceFile.Path, bytes, TestContext.Current.CancellationToken);
            files[index] = sourceFile with { PhysicalIdentity = new PlatformPhysicalFileIdentityProvider().GetIdentity(sourceFile.Path), Size = bytes.Length, ModifiedUtc = File.GetLastWriteTimeUtc(sourceFile.Path), Sha256 = SHA256.HashData(bytes) };
        }

        await _repository.WriteInventoryAsync(generation, files, CancellationToken.None);
        await _repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = generation, Status = AnalysisBatchStatus.Complete, EvidenceVersion = evidenceVersion, SummaryJson = "{}" }, CancellationToken.None);
        var directories = new ReadOnlyDirectories(() => _duringProbe?.Invoke());
        var service = new FontRenameCandidateService(_repository, new FontRenamePlanner(directories), () => _rules, Policy, TimeProvider.System, new PhysicalQuarantineFileSystem(), directories, new FontMaintenanceCoordinator());
        if (evidenceVersion == FontDuplicateAnalyzer.CurrentEvidenceVersion)
        {
            var analysis = (await _repository.GetLatestCompleteAnalysisAsync(CancellationToken.None))!;
            var prepared = await service.PrepareForPublicationAsync(analysis with { Id = 0 }, CancellationToken.None);
            await _repository.SaveRenameCandidatesAsync(prepared with { State = prepared.State with { AnalysisBatchId = analysis.Id } }, service.ReadRequirements(), null, CancellationToken.None);
        }

        return service;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class ReadOnlyDirectories(Action probe) : IFontRenameDirectoryOperations
    {
        public FontRenameDirectoryProbe Probe(string file, string root, string target) { probe(); return new(true, true, null); }
        public Task<FontRenameDirectoryResult> CreateDirectoryAsync(string path, string root, FontDirectoryPolicyResolver policy, CancellationToken token) => throw new Exception("Read must not create directories");
        public Task<FontRenameDirectoryResult> TryRemoveEmptyAsync(string path, string root, FontDirectoryPolicyResolver policy, CancellationToken token) => throw new Exception("Read must not remove directories");
    }
}
