using System.Text.Json;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;
using Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Persistence;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Maintenance;

public sealed class FontRenameCandidateRefreshQueueTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task RestartReclaimsInterruptedCalculationWithOriginalIdentity()
    {
        await using var fixture = await SqliteRenameCandidateTests.Fixture.CreateAsync();
        var queue = Queue(fixture, () => fixture.State.Rules);
        var original = await queue.QueueAsync("restart", CancellationToken.None);
        var first = Assert.Single(await fixture.Repository.ClaimRenameCandidateRefreshGroupAsync(CancellationToken.None));
        var state = (await fixture.Repository.GetRenameCandidateContextAsync(fixture.Requirements, CancellationToken.None)).State!;
        await fixture.Repository.SetRenameCandidateStateAsync(state with { Status = FontRenameCandidateGenerationStatus.Running }, fixture.Requirements, CancellationToken.None);
        await fixture.Repository.RecoverRenameCandidateRefreshAsync(CancellationToken.None);
        var recovered = Assert.Single(await fixture.Repository.ClaimRenameCandidateRefreshGroupAsync(CancellationToken.None));
        Assert.Equal(original.Id, recovered.Id);
        Assert.Equal(first.RequestJson, recovered.RequestJson);
        Assert.Equal(FontRenameCandidateGenerationStatus.Pending, (await fixture.Repository.GetRenameCandidateContextAsync(fixture.Requirements, CancellationToken.None)).State!.Status);
    }

    [Theory]
    [InlineData(FontRenameCandidateGenerationStatus.Pending)]
    [InlineData(FontRenameCandidateGenerationStatus.Running)]
    public async Task RestartRequeuesInterruptedAnalysisOwnedRepair(FontRenameCandidateGenerationStatus status)
    {
        await using var fixture = await SqliteRenameCandidateTests.Fixture.CreateAsync();
        var owner = await fixture.Repository.CreateOperationRequestAsync(new OperationRequest
        {
            Kind = FontOperationKind.Analyze, IdempotencyKey = "unchanged-scan", RequestJson = "{}"
        }, CancellationToken.None);
        await fixture.Repository.UpdateOperationAsync(owner with { Status = OperationRequestStatus.Running, ClaimedUtc = DateTimeOffset.UnixEpoch }, CancellationToken.None);
        await fixture.Repository.SetRenameCandidateStateAsync(fixture.State with { Status = status, OperationRequestId = owner.Id }, fixture.Requirements, CancellationToken.None);
        var before = await fixture.Repository.GetRenameCandidateContextAsync(fixture.Requirements, CancellationToken.None);

        await fixture.ReopenAsync();
        var queue = Queue(fixture, () => fixture.State.Rules);
        await queue.RecoverInterruptedAsync(CancellationToken.None);
        var refresh = await queue.EnsureCurrentAsync(CancellationToken.None);
        Assert.NotNull(refresh);
        Assert.Equal(FontOperationKind.RefreshRenameCandidates, refresh.Kind);
        Assert.Null(Payload(refresh).CalculationRequestId);
        var interrupted = (await fixture.Repository.GetOperationRequestAsync(owner.Id, CancellationToken.None))!;
        Assert.Equal(OperationRequestStatus.Failed, interrupted.Status);
        Assert.NotNull(interrupted.CompletedUtc);
        Assert.Contains("interrupted", interrupted.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(owner.RequestJson, interrupted.RequestJson);
        var after = await fixture.Repository.GetRenameCandidateContextAsync(fixture.Requirements, CancellationToken.None);
        Assert.Equal(before.Publication, after.Publication);
        Assert.Equal(FontRenameCandidateGenerationStatus.Pending, after.State!.Status);
        Assert.Equal(refresh.Id, after.State.OperationRequestId);
        Assert.Null(await queue.EnsureCurrentAsync(CancellationToken.None));

        var claimed = Assert.Single(await fixture.Repository.ClaimRenameCandidateRefreshGroupAsync(CancellationToken.None));
        Assert.Equal(refresh.Id, claimed.Id);
        await fixture.Repository.SaveRenameCandidatesAsync(new(fixture.State with { OperationRequestId = claimed.Id }, fixture.Items), fixture.Requirements, claimed.Id, CancellationToken.None);
        await fixture.Repository.UpdateOperationAsync(claimed with { Status = OperationRequestStatus.Complete }, CancellationToken.None);
        await fixture.ReopenAsync();
        var restored = await fixture.Repository.LoadRenameCandidatesAsync(null, fixture.Requirements, CancellationToken.None);
        Assert.True(restored.Context.IsCurrent);
        Assert.Equal(FontRenameCandidateGenerationStatus.Ready, restored.Context.State!.Status);
        Assert.Equal(fixture.Items.Length, restored.Items.Count);
        Assert.Equal(before.Publication, restored.Context.Publication);
    }

    [Fact]
    public async Task RestartPreservesCommittedCandidatesAndUnrelatedAnalysisRequests()
    {
        await using var fixture = await SqliteRenameCandidateTests.Fixture.CreateAsync();
        var owner = await fixture.Repository.CreateOperationRequestAsync(new OperationRequest { Kind = FontOperationKind.Analyze, IdempotencyKey = "committed", RequestJson = "{}" }, CancellationToken.None);
        await fixture.Repository.UpdateOperationAsync(owner with { Status = OperationRequestStatus.Running }, CancellationToken.None);
        await fixture.Repository.SetRenameCandidateStateAsync(fixture.State with { Status = FontRenameCandidateGenerationStatus.Running, OperationRequestId = owner.Id }, fixture.Requirements, CancellationToken.None);
        await fixture.Repository.SaveRenameCandidatesAsync(new(fixture.State with { OperationRequestId = owner.Id }, fixture.Items), fixture.Requirements, owner.Id, CancellationToken.None);
        await fixture.ReopenAsync();
        var queue = Queue(fixture, () => fixture.State.Rules);
        await queue.RecoverInterruptedAsync(CancellationToken.None);
        Assert.Null(await queue.EnsureCurrentAsync(CancellationToken.None));
        var loaded = await fixture.Repository.LoadRenameCandidatesAsync(null, fixture.Requirements, CancellationToken.None);
        Assert.True(loaded.Context.IsCurrent);
        Assert.Equal(FontRenameCandidateGenerationStatus.Ready, loaded.Context.State!.Status);
        Assert.Equal(fixture.Items.Length, loaded.Items.Count);
        Assert.Equal(OperationRequestStatus.Running, (await fixture.Repository.GetOperationRequestAsync(owner.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task TerminalRequestCannotLeaveCandidateStateRunning()
    {
        await using var fixture = await SqliteRenameCandidateTests.Fixture.CreateAsync();
        var queue = Queue(fixture, () => fixture.State.Rules);
        var original = await queue.QueueAsync("terminal", CancellationToken.None);
        var state = (await fixture.Repository.GetRenameCandidateContextAsync(fixture.Requirements, CancellationToken.None)).State!;
        await fixture.Repository.SetRenameCandidateStateAsync(state with { Status = FontRenameCandidateGenerationStatus.Running }, fixture.Requirements, CancellationToken.None);
        await fixture.Repository.UpdateOperationAsync(original with { Status = OperationRequestStatus.Cancelled, Error = "cancelled" }, CancellationToken.None);
        Assert.Null(await queue.EnsureCurrentAsync(CancellationToken.None));
        var after = (await fixture.Repository.GetRenameCandidateContextAsync(fixture.Requirements, CancellationToken.None)).State!;
        Assert.Equal(FontRenameCandidateGenerationStatus.Failed, after.Status);
        Assert.Equal("cancelled", after.Error);
    }

    [Fact]
    public async Task DuplicateRefreshesShareCalculationButKeepRetryIdentity()
    {
        await using var fixture = await SqliteRenameCandidateTests.Fixture.CreateAsync();
        await fixture.SaveAsync();
        var queue = Queue(fixture, () => fixture.State.Rules);
        var first = await queue.QueueAsync("first", CancellationToken.None);
        var second = await queue.QueueAsync("second", CancellationToken.None);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Null(Payload(first).CalculationRequestId);
        Assert.Equal(first.Id, Payload(second).CalculationRequestId);
        Assert.Equal(first.Id, (await queue.QueueAsync("first", CancellationToken.None)).Id);
        Assert.Equal(second.Id, (await queue.QueueAsync("second", CancellationToken.None)).Id);
        var group = await fixture.Repository.ClaimRenameCandidateRefreshGroupAsync(CancellationToken.None);
        Assert.Equal(new[] { first.Id, second.Id }, group.Select(request => request.Id));
        Assert.All(group, request => Assert.Equal(OperationRequestStatus.Running, request.Status));
        Assert.Empty(await fixture.Repository.ClaimRenameCandidateRefreshGroupAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AbaRuleChangeCannotCommitOldRequest()
    {
        await using var fixture = await SqliteRenameCandidateTests.Fixture.CreateAsync();
        await fixture.SaveAsync();
        var rules = fixture.State.Rules;
        var queue = Queue(fixture, () => rules);
        var first = await queue.QueueAsync("a1", CancellationToken.None);
        await fixture.Repository.ClaimRenameCandidateRefreshGroupAsync(CancellationToken.None);
        rules = rules with { FileNameTemplate = "{family}-changed" };
        var middle = await queue.QueueAsync("b", CancellationToken.None);
        rules = fixture.State.Rules;
        var latest = await queue.QueueAsync("a2", CancellationToken.None);
        Assert.True(latest.Id > middle.Id);
        Assert.Null(Payload(latest).CalculationRequestId);
        var old = new FontRenameCandidatePreparation(fixture.State with { OperationRequestId = first.Id }, fixture.Items);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Repository.SaveRenameCandidatesAsync(old, fixture.Requirements, first.Id, CancellationToken.None));
        Assert.Equal(latest.Id, (await fixture.Repository.GetRenameCandidateContextAsync(fixture.Requirements, CancellationToken.None)).State!.OperationRequestId);
        Assert.Equal(first.Id, (await queue.QueueAsync("a1", CancellationToken.None)).Id);
    }

    [Fact]
    public async Task RuleChangeDoesNotPublishAnalysis()
    {
        await using var fixture = await SqliteRenameCandidateTests.Fixture.CreateAsync();
        await fixture.SaveAsync();
        var before = await fixture.Repository.GetRenameCandidateContextAsync(fixture.Requirements, CancellationToken.None);
        var rules = fixture.State.Rules with { FolderTemplate = "{family}" };
        var queued = await Queue(fixture, () => rules).EnsureCurrentAsync(CancellationToken.None);
        Assert.NotNull(queued);
        var after = await fixture.Repository.GetRenameCandidateContextAsync(fixture.Requirements with { RuleFingerprint = rules.GetFingerprint() }, CancellationToken.None);
        Assert.Equal(before.Publication, after.Publication);
        Assert.Equal(FontRenameCandidateGenerationStatus.Pending, after.State!.Status);
        Assert.Equal(rules, after.State.Rules);
    }

    [Fact]
    public async Task RestartSchedulesMissingCandidates()
    {
        await using var fixture = await SqliteRenameCandidateTests.Fixture.CreateAsync();
        var queue = Queue(fixture, () => fixture.State.Rules);
        Assert.NotNull(await queue.EnsureCurrentAsync(CancellationToken.None));
        Assert.Null(await queue.EnsureCurrentAsync(CancellationToken.None));
        Assert.Single(await fixture.Repository.LoadPendingOperationKindsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FailedContextDoesNotLoopButExplicitRetryCreatesNewRoot()
    {
        await using var fixture = await SqliteRenameCandidateTests.Fixture.CreateAsync();
        var queue = Queue(fixture, () => fixture.State.Rules);
        var request = await queue.QueueAsync("failed", CancellationToken.None);
        var state = (await fixture.Repository.GetRenameCandidateContextAsync(fixture.Requirements, CancellationToken.None)).State!;
        await fixture.Repository.SetRenameCandidateStateAsync(state with { Status = FontRenameCandidateGenerationStatus.Failed, Error = "cancelled" }, fixture.Requirements, CancellationToken.None);
        await fixture.Repository.UpdateOperationAsync(request with { Status = OperationRequestStatus.Cancelled }, CancellationToken.None);
        Assert.Null(await queue.EnsureCurrentAsync(CancellationToken.None));
        var retry = await queue.QueueAsync("retry", CancellationToken.None);
        Assert.True(retry.Id > request.Id);
        Assert.Null(Payload(retry).CalculationRequestId);
    }

    [Fact]
    public async Task LateJoinedRequestUsesCompletedRootWithoutRecalculation()
    {
        await using var fixture = await SqliteRenameCandidateTests.Fixture.CreateAsync();
        var queue = Queue(fixture, () => fixture.State.Rules);
        var first = await queue.QueueAsync("root", CancellationToken.None);
        var group = await fixture.Repository.ClaimRenameCandidateRefreshGroupAsync(CancellationToken.None);
        var joined = await queue.QueueAsync("late", CancellationToken.None);
        await fixture.Repository.SaveRenameCandidatesAsync(new(fixture.State with { OperationRequestId = first.Id }, fixture.Items), fixture.Requirements, first.Id, CancellationToken.None);
        await fixture.Repository.UpdateOperationAsync(group[0] with { Status = OperationRequestStatus.Complete }, CancellationToken.None);
        Assert.Equal(first.Id, Payload(joined).CalculationRequestId);
        Assert.Equal(joined.Id, Assert.Single(await fixture.Repository.ClaimRenameCandidateRefreshGroupAsync(CancellationToken.None)).Id);
        var next = await queue.QueueAsync("new-after-complete", CancellationToken.None);
        Assert.Null(Payload(next).CalculationRequestId);
        Assert.True(next.Id > joined.Id);
    }

    [Fact]
    public async Task NoAnalysisDoesNotSchedule()
    {
        var repository = new InMemoryFontRepository();
        var rules = new FontNamingRules("{family}", "", false);
        var queue = new FontRenameCandidateRefreshQueue(repository, () => rules, () => new(rules.GetFingerprint(), "", "", 1, 1), TimeProvider.System);
        Assert.Null(await queue.EnsureCurrentAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => queue.QueueAsync("no-analysis", CancellationToken.None));
        Assert.Empty(repository.OperationRequests);
    }

    private static FontRenameCandidateRefreshQueue Queue(SqliteRenameCandidateTests.Fixture fixture, Func<FontNamingRules> rules)
        => new(fixture.Repository, rules, () => fixture.Requirements with { RuleFingerprint = rules().GetFingerprint() }, TimeProvider.System);

    internal static FontRenameCandidateRefreshPayload Payload(OperationRequest request)
        => JsonSerializer.Deserialize<FontRenameCandidateRefreshPayload>(request.RequestJson, JsonOptions)!;
}