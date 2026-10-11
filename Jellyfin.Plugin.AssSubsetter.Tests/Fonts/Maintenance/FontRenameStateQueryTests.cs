using System.Security.Cryptography;
using Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;
using Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;
using Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Persistence;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Maintenance;

public sealed class FontRenameStateQueryTests : IDisposable
{
    private static readonly System.Text.Json.JsonSerializerOptions WebJsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rename-state-" + Guid.NewGuid().ToString("N"));
    private readonly InMemoryFontRepository _repository = new();
    private readonly FontNamingRules _rules = new("{family}", "", false);
    private FontRenameCandidateService _candidates = null!;
    private FontRenameStateQuery _query = null!;

    [Fact]
    public async Task SkippedPageExposesSavedInspectionErrorWithoutNewPhysicalChecks()
    {
        await PrepareAsync();
        var saved = await _repository.LoadRenameCandidatesAsync(null, _candidates.ReadRequirements(), CancellationToken.None);
        const string error = "Only legacy cmap encoding records are present; no Unicode mapping can be proven.";
        var file = saved.Items[0].File with { Health = FontFileHealth.Unclassifiable, Error = error };
        await _repository.SaveRenameCandidatesAsync(new(saved.Context.State!, [saved.Items[0] with { File = file, SkipReason = "unclassifiable-or-link" }]), _candidates.ReadRequirements(), null, CancellationToken.None);
        _repository.ThrowOnFullRenameReads = true;
        int reads = _repository.InventoryLoadCallCount;
        var row = Assert.Single((await _query.GetPageAsync("skipped", 1, 50, CancellationToken.None)).Items);
        var json = System.Text.Json.JsonSerializer.SerializeToElement(row.Item, WebJsonOptions);
        Assert.Equal(error, json.GetProperty("inspectionError").GetString());
        Assert.Equal("unclassifiable-or-link", row.Item.Error);
        Assert.False(row.CanExecute);
        Assert.Equal(reads, _repository.InventoryLoadCallCount);
    }

    [Fact]
    public async Task RuleDriftDuringReadKeepsCompatibleAnalysisRefreshable()
    {
        await PrepareAsync();
        int reads = 0;
        var candidates = new FontRenameCandidateService(_repository, new FontRenamePlanner(new PhysicalFontRenameDirectoryOperations()),
            () => ++reads == 1 ? _rules : _rules with { FileNameTemplate = "{family}-changed" },
            Policy, TimeProvider.System, new PhysicalQuarantineFileSystem(), new PhysicalFontRenameDirectoryOperations(), new FontMaintenanceCoordinator());
        var page = await new FontRenameStateQuery(_repository, candidates).GetPageAsync("pending", 1, 50, CancellationToken.None);
        Assert.Null(page.Current);
        Assert.Equal("stale", page.CandidateState!.Status);
        Assert.True(page.CanRefreshCandidates);
        Assert.All(page.Items, row => Assert.False(row.CanExecute));
    }

    [Fact]
    public async Task RefreshAvailabilityRequiresCompatibleCurrentAnalysis()
    {
        await PrepareAsync();
        Assert.True((await _query.GetPageAsync("pending", 1, 50, CancellationToken.None)).CanRefreshCandidates);
        var missing = new FontRenameStateQuery(new InMemoryFontRepository(), _candidates);
        Assert.False((await missing.GetPageAsync("pending", 1, 50, CancellationToken.None)).CanRefreshCandidates);
        long generation = await _repository.BeginGenerationAsync(Input(), [], CancellationToken.None);
        await _repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = generation, Status = AnalysisBatchStatus.Complete, EvidenceVersion = 0, SummaryJson = "{}" }, CancellationToken.None);
        Assert.False((await _query.GetPageAsync("pending", 1, 50, CancellationToken.None)).CanRefreshCandidates);
    }

    [Fact]
    public async Task PageReadsNeverCalculateOrTouchFilesystem()
    {
        await PrepareAsync(3);
        _repository.ThrowOnFullRenameReads = true;
        int inventoryReads = _repository.InventoryLoadCallCount;
        var page = await _query.GetPageAsync("pending", 2, 1, CancellationToken.None);
        Assert.Equal(3, page.TotalCount);
        Assert.Single(page.Items);
        Assert.Equal(inventoryReads, _repository.InventoryLoadCallCount);
    }

    [Fact]
    public async Task CurrentReadsHaveExactCountsAndBoundedPagesWithoutPersisting()
    {
        await PrepareAsync(251);
        var first = await _query.GetPageAsync("pending", -3, 999, CancellationToken.None);
        var second = await _query.GetPageAsync("pending", 2, 200, CancellationToken.None);
        Assert.Equal(251, first.TotalCount);
        Assert.Equal(200, first.Items.Count);
        Assert.Equal(51, second.Items.Count);
        Assert.Equal(1, first.Page);
        Assert.Equal(200, first.PageSize);
        Assert.Empty(first.Items.Select(row => row.Key).Intersect(second.Items.Select(row => row.Key)));
        Assert.All(first.Items, row => Assert.True(row.CanExecute));
        Assert.Empty(_repository.OperationRequests);
        Assert.Empty(await _repository.LoadRenameStateItemsAsync(CancellationToken.None));
        Assert.Single((await _query.GetPageAsync("pending", 1, 0, CancellationToken.None)).Items);
        await Assert.ThrowsAsync<ArgumentException>(() => _query.GetPageAsync("unknown", 1, 50, CancellationToken.None));
    }

    [Fact]
    public async Task PendingAuthorizationIsVisibleBeforeBatchAndFailureKeepsConcreteReason()
    {
        await PrepareAsync();
        var current = await _candidates.GetCurrentAsync(CancellationToken.None);
        var request = await _candidates.CreateRequestAsync(current.GenerationId, current.AnalysisBatchId, current.RuleFingerprint, [1], "pending", CancellationToken.None);
        var pending = Assert.Single((await _query.GetPageAsync("pending", 1, 50, CancellationToken.None)).Items);
        Assert.Equal(request.Id, pending.OperationRequestId);
        Assert.Null(pending.MaintenanceBatchId);
        Assert.False(pending.CanExecute);
        await _repository.UpdateOperationAsync(request with { Status = OperationRequestStatus.Failed, Error = "immutable rules changed" }, CancellationToken.None);
        var failed = Assert.Single((await _query.GetPageAsync("failed", 1, 50, CancellationToken.None)).Items);
        Assert.Equal("immutable rules changed", failed.Item.Error);
        Assert.True(Assert.Single((await _query.GetPageAsync("pending", 1, 50, CancellationToken.None)).Items).CanExecute);
    }

    [Fact]
    public async Task ReplacementAtSamePathDoesNotInheritOldFailureOrAuthorization()
    {
        await PrepareAsync();
        var current = await _candidates.GetCurrentAsync(CancellationToken.None);
        var request = await _candidates.CreateRequestAsync(current.GenerationId, current.AnalysisBatchId, current.RuleFingerprint, [1], "old", CancellationToken.None);
        await _repository.UpdateOperationAsync(request with { Status = OperationRequestStatus.Failed, Error = "old-file-failure" }, CancellationToken.None);
        long generation = await _repository.BeginGenerationAsync(Input(), [], CancellationToken.None);
        var old = (await _repository.LoadPublishedInventoryAsync(CancellationToken.None)).Single();
        await _repository.WriteInventoryAsync(generation, [old with { GenerationId = generation, PhysicalIdentity = "replacement", Sha256 = Enumerable.Repeat((byte)7, 32).ToArray() }], CancellationToken.None);
        await PublishAsync(generation);
        var pending = Assert.Single((await _query.GetPageAsync("pending", 1, 50, CancellationToken.None)).Items);
        Assert.True(pending.CanExecute);
        Assert.Equal(generation, pending.GenerationId);
        Assert.Null(pending.OperationRequestId);
        Assert.Equal(request.Id, Assert.Single((await _query.GetPageAsync("failed", 1, 50, CancellationToken.None)).Items).OperationRequestId);
    }

    [Fact]
    public async Task BlockedHistoryRollbackAvailabilityDoesNotReadTargetEvidence()
    {
        await PrepareAsync();
        var current = await _candidates.GetCurrentAsync(CancellationToken.None);
        var request = await _candidates.CreateRequestAsync(current.GenerationId, current.AnalysisBatchId, current.RuleFingerprint, [1], "history", CancellationToken.None);
        var item = FontOperationRequestPayloadCodec.ParseRenameExecution(request.RequestJson).Items.Single();
        File.Move(item.OldPath, item.NewPath);
        var batch = await _repository.SaveMaintenanceBatchAsync(new MaintenanceBatch { Kind = FontOperationKind.NormalizeNames, OperationRequestId = request.Id, Status = MaintenanceBatchStatus.Complete, SummaryJson = "{}", RenameItems = [item with { Status = RenameItemStatus.Renamed }] }, CancellationToken.None);
        await _repository.UpdateOperationAsync(request with { Status = OperationRequestStatus.Complete }, CancellationToken.None);
        await _repository.MarkAnalysisStaleAsync(current.GenerationId, CancellationToken.None);
        var page = await _query.GetPageAsync("renamed", 1, 50, CancellationToken.None);
        Assert.Null(page.Current);
        Assert.NotNull(page.BlockReason);
        Assert.True(Assert.Single(page.Items).CanRollback);
        Assert.Equal(batch.Id, page.Items[0].MaintenanceBatchId);
        File.SetLastWriteTimeUtc(item.NewPath, item.Evidence.ModifiedUtc.UtcDateTime.AddMinutes(1));
        Assert.True(Assert.Single((await _query.GetPageAsync("renamed", 1, 50, CancellationToken.None)).Items).CanRollback);
        await File.WriteAllBytesAsync(item.NewPath, [9, 8, 7], TestContext.Current.CancellationToken);
        Assert.True(Assert.Single((await _query.GetPageAsync("renamed", 1, 50, CancellationToken.None)).Items).CanRollback);
    }

    [Theory]
    [InlineData(RenameItemStatus.FailedIo, "failed")]
    [InlineData(RenameItemStatus.InUse, "skipped")]
    [InlineData(RenameItemStatus.DestinationOccupied, "skipped")]
    public async Task TerminalHistoryDoesNotHideNewlyEligibleCandidate(RenameItemStatus status, string historyView)
    {
        await PrepareAsync();
        var current = await _candidates.GetCurrentAsync(CancellationToken.None);
        var request = await _candidates.CreateRequestAsync(current.GenerationId, current.AnalysisBatchId, current.RuleFingerprint, [1], "attempt", CancellationToken.None);
        var item = FontOperationRequestPayloadCodec.ParseRenameExecution(request.RequestJson).Items.Single();
        await _repository.SaveMaintenanceBatchAsync(new MaintenanceBatch { Kind = FontOperationKind.NormalizeNames, OperationRequestId = request.Id, Status = MaintenanceBatchStatus.Complete, SummaryJson = "{}", RenameItems = [item with { Status = status, Error = "temporary obstacle" }] }, CancellationToken.None);
        await _repository.UpdateOperationAsync(request with { Status = OperationRequestStatus.Complete }, CancellationToken.None);
        var pending = Assert.Single((await _query.GetPageAsync("pending", 1, 50, CancellationToken.None)).Items);
        Assert.True(pending.CanExecute);
        Assert.Null(pending.OperationRequestId);
        Assert.Equal("temporary obstacle", Assert.Single((await _query.GetPageAsync(historyView, 1, 50, CancellationToken.None)).Items).Item.Error);
        var retry = await _candidates.CreateRequestAsync(current.GenerationId, current.AnalysisBatchId, current.RuleFingerprint, [pending.FileId], "new-attempt", CancellationToken.None);
        var reserved = Assert.Single((await _query.GetPageAsync("pending", 1, 50, CancellationToken.None)).Items);
        Assert.Equal(retry.Id, reserved.OperationRequestId);
        Assert.False(reserved.CanExecute);
    }

    private FontDirectoryPolicyResolver Policy() => new(_root, null, null, false);
    private FontAnalysisInputSnapshot Input() => FontInventoryInputComparer.Capture([], Policy(), PhysicalFontAnalysisEvidenceSource.AnalysisCompatibilityKey);
    private async Task<AnalysisBatch> PublishAsync(long generation)
    {
        var analysis = await _repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = generation, Status = AnalysisBatchStatus.Complete, EvidenceVersion = FontDuplicateAnalyzer.CurrentEvidenceVersion, SummaryJson = "{}" }, CancellationToken.None);
        var prepared = await _candidates.PrepareForPublicationAsync(analysis with { Id = 0 }, CancellationToken.None);
        await _repository.SaveRenameCandidatesAsync(prepared with { State = prepared.State with { AnalysisBatchId = analysis.Id } }, _candidates.ReadRequirements(), null, CancellationToken.None);
        return analysis;
    }
    private async Task PrepareAsync(int count = 1)
    {
        Directory.CreateDirectory(_root);
        long generation = await _repository.BeginGenerationAsync(Input(), [], CancellationToken.None);
        var files = new List<FontFileRecord>();
        for (int i = 1; i <= count; i++)
        {
            string path = Path.Combine(_root, "old-" + i + ".ttf");
            byte[] bytes = BitConverter.GetBytes(i);
            await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
            files.Add(FontNamingTestData.File(path, FontNamingTestData.Face(names: [(1, "Example" + i)])) with { Id = i, GenerationId = generation, PhysicalIdentity = new PlatformPhysicalFileIdentityProvider().GetIdentity(path), Size = bytes.Length, ModifiedUtc = File.GetLastWriteTimeUtc(path), Sha256 = SHA256.HashData(bytes) });
        }
        await _repository.WriteInventoryAsync(generation, files, CancellationToken.None);
        _candidates = new(_repository, new FontRenamePlanner(new PhysicalFontRenameDirectoryOperations()), () => _rules, Policy, TimeProvider.System, new PhysicalQuarantineFileSystem(), new PhysicalFontRenameDirectoryOperations(), new FontMaintenanceCoordinator());
        await PublishAsync(generation);
        _query = new(_repository, _candidates);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
