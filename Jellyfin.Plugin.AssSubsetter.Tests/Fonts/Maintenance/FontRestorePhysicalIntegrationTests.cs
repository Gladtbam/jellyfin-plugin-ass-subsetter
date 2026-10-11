using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;
using Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Persistence;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Maintenance;

public sealed class FontRestorePhysicalIntegrationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RestoreAsync_ShouldPromoteVerifiedCopyAndResumeInterruptedRestore(bool interrupted, bool linkedSource)
    {
        string sourceRoot = Path.Combine(Environment.GetEnvironmentVariable("ASS_SUBSETTER_TEST_DIRECTORY") ?? Path.GetTempPath(), "ass-restore-source-" + Guid.NewGuid().ToString("N"));
        string quarantineRoot = Path.Combine(Path.GetTempPath(), "ass-restore-quarantine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(quarantineRoot);
        try
        {
            string original = Path.Combine(sourceRoot, "恢复字体.ttf");
            string quarantine = Path.Combine(quarantineRoot, "1.font");
            byte[] bytes = [1, 2, 3, 4, 5];
            await File.WriteAllBytesAsync(quarantine, bytes, TestContext.Current.CancellationToken);
            var repository = new InMemoryFontRepository();
            MaintenanceBatch batch = await repository.SaveMaintenanceBatchAsync(new MaintenanceBatch
            {
                Kind = FontOperationKind.Quarantine,
                Status = MaintenanceBatchStatus.Complete,
                StartedUtc = DateTimeOffset.UnixEpoch,
                SummaryJson = "{}"
            }, TestContext.Current.CancellationToken);
            string restoreStaging = Path.Combine(sourceRoot, ".ass-subsetter-restore-" + batch.Id.ToString("D20", System.Globalization.CultureInfo.InvariantCulture) + "-00000000000000000001");
            if (interrupted)
            {
                await File.WriteAllBytesAsync(restoreStaging, bytes, TestContext.Current.CancellationToken);
            }

            if (linkedSource)
            {
                bool linked = OperatingSystem.IsWindows()
                    ? CreateWindowsHardLink(original, restoreStaging, IntPtr.Zero)
                    : CreateUnixHardLink(restoreStaging, original) == 0;
                Assert.True(linked);
                var physical = new PhysicalQuarantineFileSystem();
                QuarantineFileEvidence originalEvidence = (await physical.ReadEvidenceAsync(original, TestContext.Current.CancellationToken))!;
                QuarantineFileEvidence stagingEvidence = (await physical.ReadEvidenceAsync(restoreStaging, TestContext.Current.CancellationToken))!;
                Assert.Equal(originalEvidence.PhysicalIdentity, stagingEvidence.PhysicalIdentity);
            }

            var manifests = new InMemoryQuarantineManifestStore();
            await manifests.SaveAsync(new QuarantineManifest
            {
                Version = 2,
                EvidenceVersion = FontDuplicateAnalyzer.CurrentEvidenceVersion,
                BatchId = batch.Id,
                OperationIdempotencyKey = "current-quarantine",
                OperationRequestJson = "{}",
                CreatedUtc = DateTimeOffset.UnixEpoch,
                Items = [new QuarantineManifestItem
                {
                    FileId = 1,
                    SourcePath = original,
                    SourcePhysicalIdentity = "original-file-id",
                    StagingPath = Path.Combine(quarantineRoot, "1.stage"),
                    QuarantinePath = quarantine,
                    Size = bytes.Length,
                    Sha256 = SHA256.HashData(bytes),
                    Replacements = [],
                    FaceReplacementProofs = [],
                    Status = interrupted ? QuarantineItemStatus.Restoring : QuarantineItemStatus.Quarantined,
                    QuarantinedUtc = DateTimeOffset.UnixEpoch
                }]
            }, TestContext.Current.CancellationToken);
            var manager = new FontQuarantineManager(repository, new FontMaintenanceCoordinator(), new PhysicalQuarantineFileSystem(), manifests, quarantineRoot, TimeProvider.System,
                () => new FontDirectoryPolicyResolver(sourceRoot, null, null, false));
            OperationRequest request = await new FontOperationRequestStore(repository, TimeProvider.System).CreateAsync(
                FontOperationKind.Restore, batch.Id, [1], "restore-real-filesystem", TestContext.Current.CancellationToken);

            MaintenanceBatch restored = await manager.RestoreAsync(request, TestContext.Current.CancellationToken);

            Assert.Equal(MaintenanceBatchStatus.Complete, restored.Status);
            Assert.Equal(QuarantineItemStatus.Restored, Assert.Single(restored.Items).Status);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(original, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(quarantine));
            Assert.False(File.Exists(restoreStaging));
            Assert.Equal(QuarantineItemStatus.Restored, Assert.Single(Assert.Single(await manifests.LoadAllAsync(TestContext.Current.CancellationToken)).Items).Status);
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
            Directory.Delete(quarantineRoot, recursive: true);
        }
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int CreateUnixHardLink([MarshalAs(UnmanagedType.LPUTF8Str)] string source, [MarshalAs(UnmanagedType.LPUTF8Str)] string destination);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateWindowsHardLink(string destination, string source, IntPtr security);
}
