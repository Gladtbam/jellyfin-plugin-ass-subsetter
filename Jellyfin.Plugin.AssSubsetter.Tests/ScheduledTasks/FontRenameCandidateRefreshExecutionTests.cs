using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;
using Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;
using Jellyfin.Plugin.AssSubsetter.ScheduledTasks;
using Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Persistence;

namespace Jellyfin.Plugin.AssSubsetter.Tests.ScheduledTasks;

public sealed class FontRenameCandidateRefreshExecutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GroupCalculatesOnceAndLateJoinedRequestReusesResult(bool late)
    {
        await using var fixture = await SqliteRenameCandidateTests.Fixture.CreateAsync();
        var queue = new FontRenameCandidateRefreshQueue(fixture.Repository, () => fixture.State.Rules, () => fixture.Requirements, TimeProvider.System);
        var first = await queue.QueueAsync("first", CancellationToken.None);
        OperationRequest? second = late ? null : await queue.QueueAsync("second", CancellationToken.None);
        int calculations = 0;
        using var executor = Executor(fixture.Repository, fixture.Requirements, async (id, token) =>
        {
            calculations++;
            if (late) second = await queue.QueueAsync("late", token);
            var prepared = new FontRenameCandidatePreparation(fixture.State with { OperationRequestId = id }, fixture.Items);
            await fixture.Repository.SaveRenameCandidatesAsync(prepared, fixture.Requirements, id, token);
            return prepared.State;
        });
        await executor.ExecutePendingOperationAsync(FontOperationKind.RefreshRenameCandidates, new InlineProgress(_ => { }), CancellationToken.None);
        if (late) await executor.ExecutePendingOperationAsync(FontOperationKind.RefreshRenameCandidates, new InlineProgress(_ => { }), CancellationToken.None);
        Assert.Equal(1, calculations);
        Assert.Equal(OperationRequestStatus.Complete, (await fixture.Repository.GetOperationRequestAsync(first.Id, CancellationToken.None))!.Status);
        Assert.Equal(OperationRequestStatus.Complete, (await fixture.Repository.GetOperationRequestAsync(second!.Id, CancellationToken.None))!.Status);
    }

    [Fact]
    public async Task CancelKeepsHistoryAndNoHalfSet()
    {
        await using var fixture = await SqliteRenameCandidateTests.Fixture.CreateAsync();
        await fixture.SaveAsync();
        var queue = new FontRenameCandidateRefreshQueue(fixture.Repository, () => fixture.State.Rules, () => fixture.Requirements, TimeProvider.System);
        var request = await queue.QueueAsync("cancel", CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        using var executor = Executor(fixture.Repository, fixture.Requirements, (_, token) =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<FontRenameCandidateState>(token);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.ExecutePendingOperationAsync(FontOperationKind.RefreshRenameCandidates, new InlineProgress(_ => { }), cancellation.Token));
        var saved = (await fixture.Repository.GetOperationRequestAsync(request.Id, CancellationToken.None))!;
        Assert.Equal(OperationRequestStatus.Cancelled, saved.Status);
        var context = await fixture.Repository.GetRenameCandidateContextAsync(fixture.Requirements, CancellationToken.None);
        Assert.Equal(FontRenameCandidateGenerationStatus.Failed, context.State!.Status);
        Assert.Equal(fixture.State.GenerationId, context.Publication!.GenerationId);
        Assert.Null(await queue.EnsureCurrentAsync(CancellationToken.None));
        Assert.Empty((await fixture.Repository.LoadRenameCandidatesAsync(null, fixture.Requirements, CancellationToken.None)).Items);
        await fixture.ExecuteAsync("UPDATE rename_candidate_state SET state_json = json_set(state_json, '$.Status', 2);");
        Assert.Equal(fixture.Items.Select(item => item.NewPath), (await fixture.Repository.LoadRenameCandidatesAsync(null, fixture.Requirements, CancellationToken.None)).Items.Select(item => item.NewPath));
    }

    internal static FontMaintenanceTaskExecutor Executor(IFontRepository repository, FontRenameCandidateReadRequirements requirements, Func<long, CancellationToken, Task<FontRenameCandidateState>> rebuild)
        => new(repository, new FontOperationRequestStore(repository, TimeProvider.System), TimeProvider.System, new RecordingFilenameNormalizer(),
            (_, _, _) => throw new InvalidOperationException("Must not scan."),
            (_, _, _) => throw new InvalidOperationException("Must not analyze."),
            (_, _, _) => throw new InvalidOperationException("Must not quarantine."),
            (_, _) => throw new InvalidOperationException("Must not restore."),
            (_, _) => throw new InvalidOperationException("Must not purge."),
            (_, _, _) => throw new InvalidOperationException("Must not publish."),
            rebuildCandidates: rebuild, candidateRequirements: () => requirements);
}
