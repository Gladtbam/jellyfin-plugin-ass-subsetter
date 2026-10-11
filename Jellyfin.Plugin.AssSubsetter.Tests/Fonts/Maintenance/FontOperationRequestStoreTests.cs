using System.Text.Json;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;
using Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Persistence;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Maintenance;

public sealed class FontOperationRequestStoreTests
{
    [Fact]
    public async Task CreateAsync_ShouldPersistSortedPhysicalIdsAndReturnExistingRequestForSameKey()
    {
        var repository = new InMemoryFontRepository();
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 8, 2, 1, 2, 3, TimeSpan.Zero));
        var store = new FontOperationRequestStore(repository, timeProvider);

        OperationRequest first = await store.CreateAsync(
            FontOperationKind.Quarantine,
            42,
            [9, 3, 9],
            "request-key",
            CancellationToken.None);
        OperationRequest second = await store.CreateAsync(
            FontOperationKind.Quarantine,
            42,
            [3, 9],
            "request-key",
            CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(timeProvider.GetUtcNow(), first.CreatedUtc);
        using JsonDocument payload = JsonDocument.Parse(first.RequestJson);
        Assert.Equal(42, payload.RootElement.GetProperty("batchId").GetInt64());
        Assert.Equal([3L, 9L], payload.RootElement.GetProperty("selectedFileIds").EnumerateArray().Select(value => value.GetInt64()));
        Assert.DoesNotContain("path", first.RequestJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateAsync_ShouldRejectReuseOfKeyForDifferentImmutablePayload()
    {
        var repository = new InMemoryFontRepository();
        var store = new FontOperationRequestStore(repository, TimeProvider.System);
        await store.CreateAsync(FontOperationKind.Quarantine, 42, [3], "request-key", CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(
            FontOperationKind.Quarantine,
            42,
            [4],
            "request-key",
            CancellationToken.None));
    }

    [Fact]
    public async Task CreateTaskAsync_ShouldPersistAnIdempotentRequestWithoutSelectedFiles()
    {
        var repository = new InMemoryFontRepository();
        var store = new FontOperationRequestStore(repository, TimeProvider.System);

        OperationRequest first = await store.CreateTaskAsync(
            FontOperationKind.Analyze,
            "scheduled-analyze",
            CancellationToken.None);
        OperationRequest second = await store.CreateTaskAsync(
            FontOperationKind.Analyze,
            "scheduled-analyze",
            CancellationToken.None);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(OperationRequestStatus.Pending, first.Status);
        Assert.Null(first.AnalysisBatchId);
        Assert.Equal("{}", first.RequestJson);
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateTaskAsync(
            FontOperationKind.Quarantine,
            "scheduled-quarantine",
            CancellationToken.None));
    }

    [Fact]
    public async Task CreateTaskAsync_ShouldAllowDerivedDataRebuildWithoutAFileSelection()
    {
        var repository = new InMemoryFontRepository();
        var store = new FontOperationRequestStore(repository, TimeProvider.System);

        OperationRequest request = await store.CreateTaskAsync(
            FontOperationKind.RebuildDerivedData,
            "rebuild-derived-data",
            CancellationToken.None);

        Assert.Equal(FontOperationKind.RebuildDerivedData, request.Kind);
        Assert.Equal("{}", request.RequestJson);
    }

    [Fact]
    public async Task IdempotencyRejectsDifferentPreviewPayloadAndInvalidRules()
    {
        var repository = new InMemoryFontRepository();
        var store = new FontOperationRequestStore(repository, TimeProvider.System);
        var rules = new FontNamingRules("{fullName}", "", false);
        var payload = new FontRenameExecutionPayload(1, 2, rules, rules.GetFingerprint(), [new RenameItemRecord { FileId = 1, Evidence = FontNamingTestData.File(Path.Combine(Path.GetTempPath(), "old.ttf")), OldPath = Path.Combine(Path.GetTempPath(), "old.ttf"), NewPath = Path.Combine(Path.GetTempPath(), "new.ttf"), SourceRoot = Path.TrimEndingDirectorySeparator(Path.GetTempPath()) }]);
        var first = await store.CreateRenameExecutionAsync(payload, "key", CancellationToken.None);
        var retry = await store.CreateRenameExecutionAsync(payload, "key", CancellationToken.None);
        Assert.Equal(first.Id, retry.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateRenameExecutionAsync(payload with { AnalysisBatchId = 3 }, "key", CancellationToken.None));
        var invalid = rules with { FileNameTemplate = "{unknown}" };
        await Assert.ThrowsAsync<ArgumentException>(() => store.CreateRenameExecutionAsync(payload with { Rules = invalid, RuleFingerprint = invalid.GetFingerprint() }, "invalid", CancellationToken.None));
        Assert.Single(repository.OperationRequests);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
