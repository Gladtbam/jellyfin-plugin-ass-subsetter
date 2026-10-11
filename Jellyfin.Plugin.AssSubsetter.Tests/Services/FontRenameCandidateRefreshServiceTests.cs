using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;
using Jellyfin.Plugin.AssSubsetter.Services;
using Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Services;

public sealed class FontRenameCandidateRefreshServiceTests
{
    [Fact]
    public async Task StartupAndRuleNotificationsScheduleInBackgroundAndUnsubscribe()
    {
        await using var fixture = await SqliteRenameCandidateTests.Fixture.CreateAsync();
        var rules = fixture.State.Rules;
        Action? changed = null;
        bool disposed = false;
        var dispatcher = new Mock<IFontOperationRequestDispatcher>();
        var queue = new FontRenameCandidateRefreshQueue(fixture.Repository, () => rules, () => fixture.Requirements with { RuleFingerprint = rules.GetFingerprint() }, TimeProvider.System);
        using var service = new FontRenameCandidateRefreshService(queue, dispatcher.Object,
            callback => { changed = callback; return new Subscription(() => disposed = true); },
            () => rules, NullLogger<FontRenameCandidateRefreshService>.Instance);
        await service.StartAsync(CancellationToken.None);
        await WaitAsync(async () => (await fixture.Repository.GetRenameCandidateContextAsync(fixture.Requirements, CancellationToken.None)).State is not null);
        Assert.NotNull(changed);
        var initial = (await fixture.Repository.GetRenameCandidateContextAsync(fixture.Requirements, CancellationToken.None)).State!;
        rules = rules with { FileNameTemplate = "{family}-new" };
        changed!();
        var currentRequirements = fixture.Requirements with { RuleFingerprint = rules.GetFingerprint() };
        await WaitAsync(async () => (await fixture.Repository.GetRenameCandidateContextAsync(currentRequirements, CancellationToken.None)).State?.RuleFingerprint == rules.GetFingerprint());
        var latest = (await fixture.Repository.GetRenameCandidateContextAsync(currentRequirements, CancellationToken.None)).State!;
        Assert.True(latest.OperationRequestId > initial.OperationRequestId);
        dispatcher.Verify(item => item.Signal(), Times.AtLeast(2));
        await service.StopAsync(CancellationToken.None);
        Assert.True(disposed);
        long? requestId = latest.OperationRequestId;
        rules = rules with { FileNameTemplate = "{family}-after-stop" };
        changed();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(requestId, (await fixture.Repository.GetRenameCandidateContextAsync(currentRequirements, CancellationToken.None)).State!.OperationRequestId);
    }

    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!await condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class Subscription(Action unsubscribe) : IDisposable
    {
        public void Dispose() => unsubscribe();
    }
}
