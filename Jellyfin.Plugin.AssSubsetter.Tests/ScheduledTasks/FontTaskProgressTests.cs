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
        executor.Setup(item => item.RefreshAnalyzeAndPruneAsync(It.IsAny<int>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
            .Returns((int _, IProgress<double> progress, CancellationToken token) =>
            {
                foreach (double value in new[] { 40d, 10d, double.NaN, -20d, 150d, 90d, 100d }) progress.Report(value);
                Assert.DoesNotContain(100d, reports);
                return Task.FromResult(new FontTaskExecutionResult(7, "analyzed", 3));
            });
        var logger = new RecordingLogger<FontCacheUpdateTask>();
        var task = new FontCacheUpdateTask(executor.Object, () => new Jellyfin.Plugin.AssSubsetter.Configuration.PluginConfiguration(), logger);

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
        executor.Setup(item => item.QuarantineAndPurgeAsync(It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
            .Returns((IProgress<double> progress, CancellationToken _) =>
            {
                progress.Report(60);
                return Task.FromResult(new FontTaskExecutionResult(9, outcome, 4));
            });
        var logger = new RecordingLogger<FontDuplicateQuarantineTask>();
        var task = new FontDuplicateQuarantineTask(executor.Object, logger);

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
        executor.Setup(item => item.QuarantineAndPurgeAsync(It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
            .Returns((IProgress<double> progress, CancellationToken _) =>
            {
                progress.Report(100);
                throw new IOException("Manifest persistence failed.");
            });
        var logger = new RecordingLogger<FontDuplicateQuarantineTask>();
        var task = new FontDuplicateQuarantineTask(executor.Object, logger);

        await Assert.ThrowsAsync<IOException>(() => task.ExecuteAsync(new InlineProgress(reports.Add), CancellationToken.None));

        Assert.Equal(new[] { 0d, 99d }, reports);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error && entry.Exception is IOException);
    }

    [Fact]
    public async Task HistoryFailureIsLoggedAndDoesNotFinishProgress()
    {
        var executor = new Mock<IFontMaintenanceTaskExecutor>();
        executor.Setup(item => item.RefreshAnalyzeAndPruneAsync(2, It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
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
        var task = new FontCacheUpdateTask(executor.Object, () => new Jellyfin.Plugin.AssSubsetter.Configuration.PluginConfiguration(), NullLogger<FontCacheUpdateTask>.Instance);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.ExecuteAsync(new InlineProgress(_ => { }), cancellation.Token));

        executor.Verify(item => item.RefreshAnalyzeAndPruneAsync(It.IsAny<int>(), It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("rename")]
    [InlineData("purge")]
    [InlineData("history")]
    public async Task OtherScheduledTasksForwardIntermediateProgress(string kind)
    {
        var reports = new List<double>();
        var executor = new Mock<IFontMaintenanceTaskExecutor>();
        Task<FontTaskExecutionResult> Execute(IProgress<double> progress)
        {
            progress.Report(35);
            progress.Report(20);
            progress.Report(100);
            Assert.DoesNotContain(100d, reports);
            return Task.FromResult(new FontTaskExecutionResult(null, "Complete"));
        }
        executor.Setup(item => item.NormalizeFilenamesAsync(It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
            .Returns((IProgress<double> progress, CancellationToken _) => Execute(progress));
        executor.Setup(item => item.QuarantineAndPurgeAsync(It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
            .Returns((IProgress<double> progress, CancellationToken _) => Execute(progress));
        executor.Setup(item => item.RefreshAnalyzeAndPruneAsync(2, It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
            .Returns(async (int _, IProgress<double> progress, CancellationToken token) =>
            {
                await Execute(progress);
                return new FontTaskExecutionResult(1, "analyzed");
            });
        MediaBrowser.Model.Tasks.IScheduledTask task = kind switch
        {
            "rename" => new FontFilenameNormalizationTask(executor.Object, NullLogger<FontFilenameNormalizationTask>.Instance),
            "purge" => new FontDuplicateQuarantineTask(executor.Object, NullLogger<FontDuplicateQuarantineTask>.Instance),
            _ => new FontCacheUpdateTask(executor.Object, () => new PluginConfiguration { FontHistoryRetentionCount = 2 }, NullLogger<FontCacheUpdateTask>.Instance)
        };

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
        executor.Setup(item => item.QuarantineAndPurgeAsync(It.IsAny<IProgress<double>>(), It.IsAny<CancellationToken>()))
            .Returns((IProgress<double> progress, CancellationToken _) =>
            {
                delayed = progress;
                progress.Report(35);
                return Task.FromResult(new FontTaskExecutionResult(9, fails ? "Failed" : "Complete", 4));
            });
        var task = new FontDuplicateQuarantineTask(executor.Object, NullLogger<FontDuplicateQuarantineTask>.Instance);
        if (fails)
            await Assert.ThrowsAsync<InvalidOperationException>(() => task.ExecuteAsync(new InlineProgress(reports.Add), CancellationToken.None));
        else
            await task.ExecuteAsync(new InlineProgress(reports.Add), CancellationToken.None);
        double[] finished = reports.ToArray();

        delayed!.Report(90);
        delayed.Report(100);

        Assert.Equal(finished, reports);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
