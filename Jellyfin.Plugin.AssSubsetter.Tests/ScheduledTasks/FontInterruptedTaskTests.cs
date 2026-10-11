using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;
using Jellyfin.Plugin.AssSubsetter.ScheduledTasks;
using Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.AssSubsetter.Tests.ScheduledTasks;

public sealed class FontInterruptedTaskTests
{
    [Fact]
    public async Task ClaimedStaleQuarantineRequestFailsWithIdentifiersAndNoCompletedProgress()
    {
        var repository = new InMemoryFontRepository();
        var store = new FontOperationRequestStore(repository, TimeProvider.System);
        var request = await repository.CreateOperationRequestAsync(new OperationRequest
        {
            Kind = FontOperationKind.Quarantine,
            Status = OperationRequestStatus.Pending,
            AnalysisBatchId = 7,
            IdempotencyKey = "stale-quarantine",
            RequestJson = "{}",
            CreatedUtc = DateTimeOffset.UtcNow
        }, CancellationToken.None);
        int quarantineCalls = 0;
        using var executor = new FontMaintenanceTaskExecutor(repository, store, TimeProvider.System, new RecordingFilenameNormalizer(),
            (_, _, _) => throw new InvalidOperationException("Must not scan."),
            (_, _, _) => throw new InvalidOperationException("Must not analyze."),
            (_, _, _) => { quarantineCalls++; throw new InvalidOperationException("Must not quarantine stale authorization."); },
            (_, _) => throw new InvalidOperationException("Must not restore."),
            (_, _) => throw new InvalidOperationException("Must not purge."),
            (_, _, _) => throw new InvalidOperationException("Must not publish."));
        var reports = new List<double>();
        var task = new FontDuplicateQuarantineTask(executor, NullLogger<FontDuplicateQuarantineTask>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => task.ExecuteAsync(new InlineProgress(reports.Add), CancellationToken.None));

        Assert.Equal(request.Id, exception.Data[FontScheduledTaskExecution.OperationRequestIdDataKey]);
        Assert.Equal(7L, exception.Data[FontScheduledTaskExecution.BatchIdDataKey]);
        Assert.Equal(OperationRequestStatus.Failed, (await repository.GetOperationRequestAsync(request.Id, CancellationToken.None))!.Status);
        Assert.Equal(0, quarantineCalls);
        Assert.DoesNotContain(100d, reports);
    }

    [Fact]
    public async Task ResumableBatchFailsTheFinishedRequestWithoutCompletingProgress()
    {
        var repository = new InMemoryFontRepository();
        var store = new FontOperationRequestStore(repository, TimeProvider.System);
        var request = await repository.CreateOperationRequestAsync(new OperationRequest
        {
            Kind = FontOperationKind.NormalizeNames,
            Status = OperationRequestStatus.Pending,
            IdempotencyKey = "interrupted-rename",
            RequestJson = "{}",
            CreatedUtc = DateTimeOffset.UtcNow
        }, CancellationToken.None);
        var normalizer = new RecordingFilenameNormalizer(operation => new MaintenanceBatch
        {
            Id = 9,
            OperationRequestId = operation.Id,
            Kind = operation.Kind,
            Status = MaintenanceBatchStatus.Running,
            StartedUtc = DateTimeOffset.UtcNow,
            CompletedUtc = null,
            SummaryJson = "{}"
        });
        using var executor = new FontMaintenanceTaskExecutor(repository, store, TimeProvider.System, normalizer,
            (_, _, _) => throw new InvalidOperationException("Must not scan."),
            (_, _, _) => throw new InvalidOperationException("Must not analyze."),
            (_, _, _) => throw new InvalidOperationException("Must not quarantine."),
            (_, _) => throw new InvalidOperationException("Must not restore."),
            (_, _) => throw new InvalidOperationException("Must not purge."),
            (_, _, _) => throw new InvalidOperationException("Must not publish."));
        var reports = new List<double>();
        var task = new FontFilenameNormalizationTask(executor, NullLogger<FontFilenameNormalizationTask>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => task.ExecuteAsync(new InlineProgress(reports.Add), CancellationToken.None));

        var finished = (await repository.GetOperationRequestAsync(request.Id, CancellationToken.None))!;
        Assert.Equal(OperationRequestStatus.Failed, finished.Status);
        Assert.NotNull(finished.CompletedUtc);
        Assert.Contains("interrupted", finished.Error);
        Assert.DoesNotContain(100d, reports);
    }
}
