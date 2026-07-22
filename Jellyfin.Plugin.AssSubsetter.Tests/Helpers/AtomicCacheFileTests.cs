using Jellyfin.Plugin.AssSubsetter.Helpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Helpers;

public sealed class AtomicCacheFileTests : IDisposable
{
    private readonly string _tempDir = Path.Join(Path.GetTempPath(), $"ass-subsetter-atomic-{Guid.NewGuid():N}");

    public AtomicCacheFileTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task WriteAsync_ShouldReplaceDestinationOnlyAfterWriterSucceeds()
    {
        string output = Path.Join(_tempDir, "subtitle.ass");
        await File.WriteAllTextAsync(output, "old-cache", TestContext.Current.CancellationToken);

        bool result = await AtomicCacheFile.WriteAsync(
            output,
            async (partial, token) =>
            {
                Assert.Equal(_tempDir, Path.GetDirectoryName(partial));
                Assert.Equal("old-cache", await File.ReadAllTextAsync(output, token));
                await File.WriteAllTextAsync(partial, "new-cache", token);
                return true;
            },
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.Equal("new-cache", await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_tempDir, "*.partial.*"));
    }

    [Fact]
    public async Task WriteAsync_ShouldPreserveDestinationAndCleanPartial_WhenWriterFails()
    {
        string output = Path.Join(_tempDir, "subtitle.sup");
        await File.WriteAllTextAsync(output, "old-cache", TestContext.Current.CancellationToken);

        bool result = await AtomicCacheFile.WriteAsync(
            output,
            async (partial, token) =>
            {
                await File.WriteAllTextAsync(partial, "partial-cache", token);
                return false;
            },
            NullLogger.Instance,
            TestContext.Current.CancellationToken);

        Assert.False(result);
        Assert.Equal("old-cache", await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_tempDir, "*.partial.*"));
    }

    [Fact]
    public async Task WriteAsync_ShouldPreserveDestinationAndCleanPartial_WhenCancelled()
    {
        string output = Path.Join(_tempDir, "subtitle.ass");
        await File.WriteAllTextAsync(output, "old-cache", TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAsync<OperationCanceledException>(() => AtomicCacheFile.WriteAsync(
            output,
            async (partial, token) =>
            {
                await File.WriteAllTextAsync(partial, "partial-cache", token);
                await cts.CancelAsync();
                token.ThrowIfCancellationRequested();
                return true;
            },
            NullLogger.Instance,
            cts.Token));

        Assert.Equal("old-cache", await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_tempDir, "*.partial.*"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }
}
