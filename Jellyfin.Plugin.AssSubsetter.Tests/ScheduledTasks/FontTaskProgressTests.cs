using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.ScheduledTasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Jellyfin.Plugin.AssSubsetter.Tests.ScheduledTasks;

public sealed class FontTaskProgressTests
{
    [Fact]
    public async Task AnalysisProgressRemainsMonotonicAndCompletesOnlyAfterExecution()
    {
        var reports = new List<double>();
        var executor = new Mock<IFontMaintenanceTaskExecutor>();
        executor.Setup(item => item.RunScheduledMaintenanceAsync(FontMaintenanceStages.Analyze, It.IsAny<int>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
            .Returns((FontMaintenanceStages _, int _, IProgress<double> progress, CancellationToken _) =>
            {
                foreach (double value in new[] { 40d, 10d, double.NaN, -20d, 150d, 90d, 100d }) progress.Report(value);
                Assert.DoesNotContain(100d, reports);
                return Task.FromResult(new FontTaskExecutionResult(7, "analyzed", 3));
            });
        var logger = new RecordingLogger<FontCacheUpdateTask>();
        var task = new FontCacheUpdateTask(executor.Object, () => new PluginConfiguration(), logger);

        await task.ExecuteAsync(new InlineProgress(reports.Add), CancellationToken.None);

        Assert.Equal(new[] { 0d, 40d, 99d, 100d }, reports);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Information && entry.Message.Contains("ElapsedMs", StringComparison.Ordinal) && entry.Message.Contains("RequestId: 3", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Cancelled")]
    [InlineData("Failed")]
    public async Task ReturnedUnsuccessfulBatchDoesNotCompleteProgress(string outcome)
    {
        var reports = new List<double>();
        var executor = new Mock<IFontMaintenanceTaskExecutor>();
        executor.Setup(item => item.RunScheduledMaintenanceAsync(FontMaintenanceStages.Quarantine, It.IsAny<int>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
            .Returns((FontMaintenanceStages _, int _, IProgress<double> progress, CancellationToken _) =>
            {
                progress.Report(60);
                return Task.FromResult(new FontTaskExecutionResult(9, outcome, 4));
            });
        var logger = new RecordingLogger<FontCacheUpdateTask>();
        var task = CreateQuarantineTask(executor.Object, logger);

        Exception exception = outcome == "Cancelled"
            ? await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.ExecuteAsync(new InlineProgress(reports.Add), CancellationToken.None))
            : await Assert.ThrowsAsync<InvalidOperationException>(() => task.ExecuteAsync(new InlineProgress(reports.Add), CancellationToken.None));

        Assert.Equal(4L, exception.Data[FontScheduledTaskExecution.OperationRequestIdDataKey]);
        Assert.Equal(9L, exception.Data[FontScheduledTaskExecution.BatchIdDataKey]);
        Assert.Equal(new[] { 0d, 60d }, reports);
        Assert.Contains(logger.Entries, entry => entry.Message.Contains("ElapsedMs", StringComparison.Ordinal) && entry.Message.Contains("BatchId: 9", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExceptionAfterPerFileCompletionDoesNotReportTaskCompletion()
    {
        var reports = new List<double>();
        var executor = new Mock<IFontMaintenanceTaskExecutor>();
        executor.Setup(item => item.RunScheduledMaintenanceAsync(FontMaintenanceStages.Quarantine, It.IsAny<int>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
            .Returns((FontMaintenanceStages _, int _, IProgress<double> progress, CancellationToken _) =>
            {
                progress.Report(100);
                throw new IOException("Manifest persistence failed.");
            });
        var logger = new RecordingLogger<FontCacheUpdateTask>();
        var task = CreateQuarantineTask(executor.Object, logger);

        await Assert.ThrowsAsync<IOException>(() => task.ExecuteAsync(new InlineProgress(reports.Add), CancellationToken.None));

        Assert.Equal(new[] { 0d, 99d }, reports);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Exception is IOException);
    }

    [Fact]
    public async Task HistoryFailureIsLoggedAndDoesNotFinishProgress()
    {
        var executor = new Mock<IFontMaintenanceTaskExecutor>();
        executor.Setup(item => item.RunScheduledMaintenanceAsync(FontMaintenanceStages.Analyze, 2, It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Pruning failed."));
        var logger = new RecordingLogger<FontCacheUpdateTask>();
        var task = new FontCacheUpdateTask(executor.Object, () => new PluginConfiguration { FontHistoryRetentionCount = 2 }, logger);
        var reports = new List<double>();

        await Assert.ThrowsAsync<IOException>(() => task.ExecuteAsync(new InlineProgress(reports.Add), CancellationToken.None));

        Assert.DoesNotContain(100d, reports);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Exception is IOException);
    }

    [Fact]
    public async Task PreCancelledTaskDoesNotInvokeExecutor()
    {
        var executor = new Mock<IFontMaintenanceTaskExecutor>();
        var task = new FontCacheUpdateTask(executor.Object, () => new PluginConfiguration(), NullLogger<FontCacheUpdateTask>.Instance);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.ExecuteAsync(new InlineProgress(_ => { }), cancellation.Token));

        executor.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task PartialQuarantineBatchReportsIntermediateProgressAndCompletes()
    {
        var reports = new List<double>();
        var executor = new Mock<IFontMaintenanceTaskExecutor>();
        executor.Setup(item => item.RunScheduledMaintenanceAsync(FontMaintenanceStages.Quarantine, It.IsAny<int>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
            .Returns((FontMaintenanceStages _, int _, IProgress<double> progress, CancellationToken _) =>
            {
                progress.Report(35);
                progress.Report(20);
                progress.Report(100);
                Assert.DoesNotContain(100d, reports);
                return Task.FromResult(new FontTaskExecutionResult(1, "Partial"));
            });
        var task = CreateQuarantineTask(executor.Object, NullLogger<FontCacheUpdateTask>.Instance);

        await task.ExecuteAsync(new InlineProgress(reports.Add), CancellationToken.None);

        Assert.Equal(new[] { 0d, 35d, 99d, 100d }, reports);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayedCallbacksCannotChangeFinishedProgress(bool fails)
    {
        var reports = new List<double>();
        IProgress<double>? delayed = null;
        var executor = new Mock<IFontMaintenanceTaskExecutor>();
        executor.Setup(item => item.RunScheduledMaintenanceAsync(FontMaintenanceStages.Quarantine, It.IsAny<int>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
            .Returns((FontMaintenanceStages _, int _, IProgress<double> progress, CancellationToken _) =>
            {
                delayed = progress;
                progress.Report(35);
                return Task.FromResult(new FontTaskExecutionResult(9, fails ? "Failed" : "Complete", 4));
            });
        var task = CreateQuarantineTask(executor.Object, NullLogger<FontCacheUpdateTask>.Instance);
        if (fails)
            await Assert.ThrowsAsync<InvalidOperationException>(() => task.ExecuteAsync(new InlineProgress(reports.Add), CancellationToken.None));
        else
            await task.ExecuteAsync(new InlineProgress(reports.Add), CancellationToken.None);
        double[] finished = reports.ToArray();

        delayed!.Report(90);
        delayed.Report(100);

        Assert.Equal(finished, reports);
    }

    private static FontCacheUpdateTask CreateQuarantineTask(IFontMaintenanceTaskExecutor executor, ILogger<FontCacheUpdateTask> logger)
        => new(executor, () => new PluginConfiguration
        {
            EnableScheduledFontAnalysis = false,
            EnableScheduledFontQuarantine = true
        }, logger);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
