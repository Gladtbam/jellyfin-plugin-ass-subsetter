using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Maintenance;

public sealed class PhysicalQuarantineFileSystemTests
{
    [Fact]
    public async Task MoveForRestoreAsync_ShouldPreserveBytesWithoutRenameNoReplaceSupport()
    {
        string root = TemporaryRoot();
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "restore.stage");
        string destination = Path.Combine(root, "恢复字体.ttf");
        await File.WriteAllBytesAsync(source, [1, 2, 3, 4], TestContext.Current.CancellationToken);
        try
        {
            await new PhysicalQuarantineFileSystem().MoveForRestoreAsync(source, destination, TestContext.Current.CancellationToken);

            Assert.False(File.Exists(source));
            Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
    [Fact]
    public async Task CopyAsync_ShouldCreateVerifiedCopyWithoutOverwriting()
    {
        string root = TemporaryRoot();
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "source.ttf");
        string destination = Path.Combine(root, "copy.ttf");
        await File.WriteAllBytesAsync(source, [1, 2, 3, 4], TestContext.Current.CancellationToken);
        var fileSystem = new PhysicalQuarantineFileSystem();
        try
        {
            await fileSystem.CopyAsync(source, destination, TestContext.Current.CancellationToken);

            QuarantineFileEvidence? evidence = await fileSystem.ReadEvidenceAsync(
                destination,
                TestContext.Current.CancellationToken);
            Assert.NotNull(evidence);
            Assert.Equal(4, evidence.Size);
            await Assert.ThrowsAsync<IOException>(() => fileSystem.CopyAsync(
                source,
                destination,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveAsync_ShouldRefuseOccupiedDestination(bool restore)
    {
        string root = TemporaryRoot();
        Directory.CreateDirectory(root);
        string source = Path.Combine(root, "source.ttf");
        string destination = Path.Combine(root, "occupied.ttf");
        await File.WriteAllBytesAsync(source, [1], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(destination, [2], TestContext.Current.CancellationToken);
        var fileSystem = new PhysicalQuarantineFileSystem();
        try
        {
            await Assert.ThrowsAsync<IOException>(() => restore ? fileSystem.MoveForRestoreAsync(
                source,
                destination,
                TestContext.Current.CancellationToken) : fileSystem.MoveAsync(source, destination, TestContext.Current.CancellationToken));

            Assert.True(File.Exists(source));
            Assert.Equal([2], await File.ReadAllBytesAsync(destination, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveAsyncAcrossLinuxMountsCannotCopyAndDelete(bool restore)
    {
        if (!OperatingSystem.IsLinux()) return;
        string source = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".ttf");
        string target = Path.Combine("/dev/shm", Guid.NewGuid().ToString("N") + ".ttf");
        await File.WriteAllBytesAsync(source, [1, 2, 3], TestContext.Current.CancellationToken);
        try
        {
            var fileSystem = new PhysicalQuarantineFileSystem();
            await Assert.ThrowsAsync<IOException>(() => restore ? fileSystem.MoveForRestoreAsync(source, target, CancellationToken.None) : fileSystem.MoveAsync(source, target, CancellationToken.None));
            Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(source, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(target));
        }
        finally
        {
            File.Delete(source);
            File.Delete(target);
        }
    }
    private static string TemporaryRoot()
        => Path.Combine(Environment.GetEnvironmentVariable("ASS_SUBSETTER_TEST_DIRECTORY") ?? Path.GetTempPath(), nameof(PhysicalQuarantineFileSystemTests), Guid.NewGuid().ToString("N"));
}
