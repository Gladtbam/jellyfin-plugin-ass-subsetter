using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance.Naming;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.AssSubsetter.Fonts.Api;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;
using Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Moq;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Api;

/// <summary>Verifies the elevated font-operation HTTP boundary persists safe requests before scheduling work.</summary>
public sealed class FontManagementControllerTests
{
    [Fact]
    public async Task RefreshCandidatesPersistsBeforeSignalAndRetriesKeepOriginalIdentity()
    {
        var repository = new InMemoryFontRepository();
        long analysisId = await SeedCurrentAnalysisAsync(repository, []);
        var rules = new FontNamingRules("{family}", "", false);
        var queue = new FontRenameCandidateRefreshQueue(repository, () => rules, () => new(rules.GetFingerprint(), "", "", 1, 1), TimeProvider.System);
        var dispatcher = new Mock<IFontOperationRequestDispatcher>();
        dispatcher.Setup(item => item.Signal()).Callback(() => Assert.Single(repository.OperationRequests));
        var controller = CreateController(repository, dispatcher.Object, queue: queue);
        var first = GetAccepted(await controller.QueueRefreshRenameCandidatesAsync(new("refresh"), CancellationToken.None));
        rules = rules with { FileNameTemplate = "{family}-new" };
        var retry = GetAccepted(await controller.QueueRefreshRenameCandidatesAsync(new("refresh"), CancellationToken.None));
        Assert.Equal(first.Id, retry.Id);
        Assert.Equal(analysisId, Assert.Single(repository.OperationRequests).AnalysisBatchId);
        Assert.Equal(FontOperationKind.RefreshRenameCandidates, Assert.Single(repository.OperationRequests).Kind);
        dispatcher.Verify(item => item.Signal(), Times.Exactly(2));
    }

    [Theory]
    [InlineData(FontOperationKind.Analyze)]
    [InlineData(FontOperationKind.RebuildDerivedData)]
    [InlineData(FontOperationKind.Purge)]
    public async Task TaskRequestsWakeDurableSchedulingOnlyAfterPersistence(FontOperationKind kind)
    {
        var repository = new InMemoryFontRepository();
        var dispatcher = new Mock<IFontOperationRequestDispatcher>(MockBehavior.Strict);
        dispatcher.Setup(item => item.Signal()).Callback(() =>
        {
            OperationRequest persisted = Assert.Single(repository.OperationRequests);
            Assert.Equal(kind, persisted.Kind);
            Assert.Equal(OperationRequestStatus.Pending, persisted.Status);
        });
        var controller = CreateController(repository, dispatcher.Object);
        var request = new QueueTaskRequest("task-request");
        var response = kind switch
        {
            FontOperationKind.Analyze => await controller.QueueAnalyzeAsync(request, CancellationToken.None),
            FontOperationKind.RebuildDerivedData => await controller.QueueRebuildDerivedDataAsync(request, CancellationToken.None),
            _ => await controller.QueuePurgeAsync(request, CancellationToken.None)
        };
        Assert.Equal(Assert.Single(repository.OperationRequests).Id, GetAccepted(response).Id);
        dispatcher.Verify(item => item.Signal(), Times.Once);
    }

    [Fact]
    public void Controller_ShouldRequireElevationForEveryAction()
    {
        Type controller = typeof(FontManagementController);

        Assert.Contains(controller.GetCustomAttributes<AuthorizeAttribute>(), attribute => attribute.Policy == "RequiresElevation");
        Assert.All(
            controller.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly),
            method => Assert.True(method.GetCustomAttributes<HttpMethodAttribute>().Any()));
    }

    [Fact]
    public async Task GetReportsAsync_ShouldReturnBadRequestForNonAllowlistedFilters()
    {
        var repository = new InMemoryFontRepository();
        var controller = CreateController(repository, Mock.Of<IFontOperationRequestDispatcher>());

        ActionResult<PagedResult<FontReportItemDto>> result = await controller.GetReportsAsync(
            relationship: "drop-table",
            cancellationToken: CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task QueueSelectedQuarantineAsync_ShouldKeepAnEmptySelectionEmptyAndNotQueue()
    {
        var repository = new InMemoryFontRepository();
        var manager = new Mock<IFontOperationRequestDispatcher>(MockBehavior.Strict);
        var controller = CreateController(repository, manager.Object);

        ActionResult<OperationRequestDto> result = await controller.QueueSelectedQuarantineAsync(
            new QueueSelectedQuarantineRequest(42, [], "empty-selection"),
            CancellationToken.None);

        ConflictObjectResult conflict = Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Empty(repository.OperationRequests);
        Assert.NotNull(conflict.Value);
    }

    [Fact]
    public async Task QueueSelectedQuarantineAsync_ShouldPersistBeforeQueueingAndReturnTheStoredRequestForRetries()
    {
        var repository = new InMemoryFontRepository();
        long batchId = await SeedCurrentAnalysisAsync(repository, [3, 9]);
        Mock<IFontOperationRequestDispatcher> manager = new(MockBehavior.Strict);
        manager.Setup(taskManager => taskManager.Signal())
            .Callback(() => Assert.Single(repository.OperationRequests));
        var controller = CreateController(repository, manager.Object);
        var request = new QueueSelectedQuarantineRequest(batchId, [9, 3], "selected-duplicates");

        ActionResult<OperationRequestDto> first = await controller.QueueSelectedQuarantineAsync(request, CancellationToken.None);
        ActionResult<OperationRequestDto> second = await controller.QueueSelectedQuarantineAsync(request, CancellationToken.None);

        Assert.Equal(GetAccepted(first).Id, GetAccepted(second).Id);
        OperationRequest persisted = Assert.Single(repository.OperationRequests);
        using JsonDocument payload = JsonDocument.Parse(persisted.RequestJson);
        Assert.Equal([3L, 9L], payload.RootElement.GetProperty("selectedFileIds").EnumerateArray().Select(value => value.GetInt64()));
        manager.Verify(taskManager => taskManager.Signal(), Times.Exactly(2));
    }

    [Fact]
    public async Task QueueAllVerifiedQuarantineAsync_ShouldRequireAnExplicitConfirmation()
    {
        var repository = new InMemoryFontRepository();
        var controller = CreateController(repository, Mock.Of<IFontOperationRequestDispatcher>());

        ActionResult<OperationRequestDto> result = await controller.QueueAllVerifiedQuarantineAsync(
            new QueueAllVerifiedQuarantineRequest(42, false, "all-verified"),
            CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Empty(repository.OperationRequests);
    }

    [Fact]
    public async Task QueueSelectedQuarantineAsync_ShouldReturnThePersistedRequestWhenItsAnalysisBecomesStale()
    {
        var repository = new InMemoryFontRepository();
        long batchId = await SeedCurrentAnalysisAsync(repository, [3]);
        Mock<IFontOperationRequestDispatcher> manager = new(MockBehavior.Strict);
        manager.Setup(taskManager => taskManager.Signal());
        var controller = CreateController(repository, manager.Object);
        var request = new QueueSelectedQuarantineRequest(batchId, [3], "retry-after-stale");

        ActionResult<OperationRequestDto> first = await controller.QueueSelectedQuarantineAsync(request, CancellationToken.None);
        await repository.MarkAnalysisStaleAsync(1, CancellationToken.None);
        ActionResult<OperationRequestDto> retry = await controller.QueueSelectedQuarantineAsync(request, CancellationToken.None);

        Assert.Equal(GetAccepted(first).Id, GetAccepted(retry).Id);
        Assert.Single(repository.OperationRequests);
        manager.Verify(taskManager => taskManager.Signal(), Times.Exactly(2));
    }

    [Fact]
    public async Task QueueAllVerifiedQuarantineAsync_ShouldReturnThePersistedRequestWhenItsAnalysisBecomesStale()
    {
        var repository = new InMemoryFontRepository();
        long batchId = await SeedCurrentAnalysisAsync(repository, [3]);
        Mock<IFontOperationRequestDispatcher> manager = new(MockBehavior.Strict);
        manager.Setup(taskManager => taskManager.Signal());
        var controller = CreateController(repository, manager.Object);
        var request = new QueueAllVerifiedQuarantineRequest(batchId, true, "all-verified-after-stale");

        ActionResult<OperationRequestDto> first = await controller.QueueAllVerifiedQuarantineAsync(request, CancellationToken.None);
        await repository.MarkAnalysisStaleAsync(1, CancellationToken.None);
        ActionResult<OperationRequestDto> retry = await controller.QueueAllVerifiedQuarantineAsync(request, CancellationToken.None);

        Assert.Equal(GetAccepted(first).Id, GetAccepted(retry).Id);
        Assert.Single(repository.OperationRequests);
        manager.Verify(taskManager => taskManager.Signal(), Times.Exactly(2));
    }

    [Fact]
    public async Task QueueRestoreAsync_ShouldReturnThePersistedRequestWhenItsSourceBatchNoLongerValidates()
    {
        var repository = new InMemoryFontRepository();
        MaintenanceBatch batch = await SeedQuarantineBatchAsync(repository, 9);
        Mock<IFontOperationRequestDispatcher> manager = new(MockBehavior.Strict);
        var dispatcher = new Mock<IFontRestoreRequestDispatcher>(MockBehavior.Strict);
        dispatcher.Setup(item => item.Signal());
        var controller = CreateController(repository, manager.Object, dispatcher.Object);
        var request = new QueueMaintenanceSelectionRequest(batch.Id, [9], "restore-after-change");

        ActionResult<OperationRequestDto> first = await controller.QueueRestoreAsync(request, CancellationToken.None);
        await repository.UpdateMaintenanceBatchAsync(batch with { Kind = FontOperationKind.NormalizeNames }, CancellationToken.None);
        ActionResult<OperationRequestDto> retry = await controller.QueueRestoreAsync(request, CancellationToken.None);

        Assert.Equal(GetAccepted(first).Id, GetAccepted(retry).Id);
        Assert.Single(repository.OperationRequests, operation => operation.Kind == FontOperationKind.Restore);
        dispatcher.Verify(item => item.Signal(), Times.Exactly(2));
    }

    [Fact]
    public async Task ValidationReturnsCanonicalDefaultWithoutMutation()
    {
        var repository = new InMemoryFontRepository();
        var manager = new Mock<IFontOperationRequestDispatcher>();
        var controller = CreateController(repository, manager.Object);
        var invalid = controller.ValidateNamingRules(new FontNamingRules("{unknown}", "", false));
        Assert.NotEmpty(invalid.Errors);
        Assert.Equal(FontNamingRules.DefaultFileNameTemplate, invalid.DefaultFileNameTemplate);
        var valid = controller.ValidateNamingRules(new FontNamingRules("{fullName}", "{family}", true));
        Assert.Empty(valid.Errors);
        Assert.Empty(repository.OperationRequests);
        manager.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task DirectoryResultsReturnPersistedWarningsAndRejectOtherBatchKinds()
    {
        var repository = new InMemoryFontRepository();
        var records = new[] { new RenameDirectoryRecord("/fonts/old", "/fonts", RenameDirectoryAction.RemoveEmpty, RenameDirectoryStatus.Warning, false, "permission denied") };
        var batch = await repository.SaveMaintenanceBatchAsync(new MaintenanceBatch { Kind = FontOperationKind.NormalizeNames, SummaryJson = JsonSerializer.Serialize(new { Directories = records }) }, CancellationToken.None);
        var controller = CreateController(repository, Mock.Of<IFontOperationRequestDispatcher>());
        var result = Assert.IsType<OkObjectResult>((await controller.GetRenameDirectoriesAsync(batch.Id, CancellationToken.None)).Result);
        var item = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<RenameDirectoryDto>>(result.Value));
        Assert.Equal("/fonts/old", item.Path);
        Assert.Equal("RemoveEmpty", item.Action);
        Assert.Equal("Warning", item.Status);
        Assert.Equal("permission denied", item.Error);
        var other = await repository.SaveMaintenanceBatchAsync(new MaintenanceBatch { Kind = FontOperationKind.Quarantine, SummaryJson = "{}" }, CancellationToken.None);
        Assert.IsType<NotFoundResult>((await controller.GetRenameDirectoriesAsync(other.Id, CancellationToken.None)).Result);
    }

    [Theory]
    [InlineData("{family}", 2)]
    [InlineData("{resolvedName}{styleSuffix}{collectionSuffix}", 1)]
    [InlineData("{isCollection}", 2)]
    public void ValidationAlwaysReturnsCanonicalCatalog(string template, int version)
    {
        var rules = new FontNamingRules(template, "", false, version);
        var result = CreateController(new InMemoryFontRepository(), Mock.Of<IFontOperationRequestDispatcher>()).ValidateNamingRules(rules);
        Assert.Equal(2, result.FormatVersion);
        Assert.Equal(FontNamingRules.DefaultFileNameTemplate, result.DefaultFileNameTemplate);
        Assert.Equal(FontNamingFieldCatalog.Fields, result.Fields);
        Assert.Equal(template, rules.FileNameTemplate);
        Assert.Equal(version, rules.FormatVersion);
        Assert.Equal(template == "{family}", result.Errors.Count == 0);
    }
    private static FontManagementController CreateController(InMemoryFontRepository repository, IFontOperationRequestDispatcher taskManager, IFontRestoreRequestDispatcher? restoreDispatcher = null, Func<FontNamingRules>? rules = null, FontRenameCandidateRefreshQueue? queue = null)
    {
        var requestStore = new FontOperationRequestStore(repository, TimeProvider.System);
        var candidates = new FontRenameCandidateService(repository, new FontRenamePlanner(new PhysicalFontRenameDirectoryOperations()), () => rules?.Invoke() ?? new(FontNamingRules.DefaultFileNameTemplate, "", false), () => new FontDirectoryPolicyResolver(Path.GetTempPath(), null, null, false), TimeProvider.System, new PhysicalQuarantineFileSystem(), new PhysicalFontRenameDirectoryOperations(), new FontMaintenanceCoordinator());
        var refreshQueue = queue ?? new FontRenameCandidateRefreshQueue(repository, () => rules?.Invoke() ?? new(FontNamingRules.DefaultFileNameTemplate, "", false), candidates.ReadRequirements, TimeProvider.System);
        return new FontManagementController(new FontManagementQuery(repository), requestStore, repository, taskManager, restoreDispatcher ?? Mock.Of<IFontRestoreRequestDispatcher>(), candidates, refreshQueue);
    }

    private static OperationRequestDto GetAccepted(ActionResult<OperationRequestDto> result)
    {
        AcceptedResult accepted = Assert.IsType<AcceptedResult>(result.Result);
        return Assert.IsType<OperationRequestDto>(accepted.Value);
    }

    private static async Task<long> SeedCurrentAnalysisAsync(InMemoryFontRepository repository, IReadOnlyList<long> candidateFileIds)
    {
        long generationId = await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        FontFileRecord[] files = candidateFileIds.Select(fileId => new FontFileRecord
        {
            Id = fileId,
            GenerationId = generationId,
            Path = $"font-{fileId}.ttf",
            NormalizedPath = $"FONT-{fileId}.TTF",
            PhysicalIdentity = $"physical-{fileId}",
            Sha256 = new byte[] { 1 },
            Health = FontFileHealth.Healthy,
            Faces =
            [
                new FontFaceRecord
                {
                    Id = fileId * 10,
                    FaceIndex = 0,
                    StrictFingerprint = new byte[] { 2 },
                    FamilyName = "Example",
                    StyleName = "Regular",
                    Names = ["Example"],
                    Technology = "TrueType"
                }
            ]
        }).ToArray();
        await repository.WriteInventoryAsync(generationId, files, CancellationToken.None);

        AnalysisBatch batch = await repository.PublishAnalysisAsync(new AnalysisBatch
        {
            GenerationId = generationId,
            Status = AnalysisBatchStatus.Complete,
            EvidenceVersion = 1,
            StartedUtc = DateTimeOffset.UtcNow,
            CompletedUtc = DateTimeOffset.UtcNow,
            SummaryJson = "{}",
            Groups =
            [
                new DuplicateGroupRecord
                {
                    RetainedFaceId = 1,
                    EvidenceJson = "{}",
                    Members = candidateFileIds.Select(fileId => new DuplicateGroupMemberRecord
                    {
                        FaceId = fileId * 10,
                        FileId = fileId,
                        Decision = DuplicateGroupMemberDecision.QuarantineCandidate,
                        ScoreJson = "{}",
                        Reason = "candidate"
                    }).ToArray()
                }
            ]
        }, CancellationToken.None);
        return batch.Id;
    }

    private static async Task<MaintenanceBatch> SeedQuarantineBatchAsync(InMemoryFontRepository repository, long fileId)
    {
        OperationRequest operation = await repository.CreateOperationRequestAsync(new OperationRequest
        {
            Kind = FontOperationKind.Quarantine,
            Status = OperationRequestStatus.Complete,
            IdempotencyKey = "quarantine-source-" + fileId,
            RequestJson = "{}",
            CreatedUtc = DateTimeOffset.UtcNow
        }, CancellationToken.None);
        return await repository.SaveMaintenanceBatchAsync(new MaintenanceBatch
        {
            OperationRequestId = operation.Id,
            Kind = FontOperationKind.Quarantine,
            Status = MaintenanceBatchStatus.Complete,
            StartedUtc = DateTimeOffset.UtcNow,
            SummaryJson = "{}",
            Items =
            [
                new QuarantineItemRecord
                {
                    FileId = fileId,
                    SourcePath = "source.ttf",
                    StagingPath = "staging.ttf",
                    QuarantinePath = "quarantine.ttf",
                    Sha256 = new byte[] { 1 }
                }
            ]
        }, CancellationToken.None);
    }
}
