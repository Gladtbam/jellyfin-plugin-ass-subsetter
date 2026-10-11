using System.Text.Json;
using Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;
using Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;
using Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Persistence;
using Jellyfin.Plugin.AssSubsetter.Models;
using Jellyfin.Plugin.AssSubsetter.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Maintenance;

public sealed class FontQuarantineManagerTests
{
    [Fact]
    public async Task PurgeDoesNotCountAnItemWhoseDeletionFails()
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: true, duplicateCount: 2);
        MaintenanceBatch quarantined = await fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None);
        fixture.TimeProvider.UtcNow = fixture.TimeProvider.UtcNow.AddDays(30);
        string blockedPath = quarantined.Items[0].QuarantinePath;
        fixture.FileSystem.DeniedSourceRemovalPath = blockedPath;
        var reports = new List<double>();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Manager.PurgeExpiredAsync(CancellationToken.None, new RecordingProgress(reports.Add)));

        Assert.Equal(new[] { 0d }, reports);
        Assert.True(fixture.FileSystem.FileExists(blockedPath));
    }

    [Theory]
    [InlineData(29, 0)]
    [InlineData(30, 2)]
    public async Task PurgeReportsEveryInspectedFileIncludingUnexpiredItems(int days, int expectedPurged)
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: true, duplicateCount: 2);
        await fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None);
        fixture.TimeProvider.UtcNow = fixture.TimeProvider.UtcNow.AddDays(days);
        var reports = new List<double>();

        int purged = await fixture.Manager.PurgeExpiredAsync(CancellationToken.None, new RecordingProgress(reports.Add));

        Assert.Equal(expectedPurged, purged);
        Assert.Contains(50d, reports);
        Assert.Equal(100d, reports[^1]);
        Assert.Equal(reports.OrderBy(value => value), reports);
    }

    [Fact]
    public async Task QuarantineAsync_ShouldRejectUnsupportedAnalysisBeforeMovingAFile()
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: true, evidenceVersion: 1);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None));

        Assert.True(fixture.FileSystem.FileExists(fixture.SourcePath));
        Assert.Equal(0, fixture.FileSystem.MoveCount);
    }

    [Fact]
    public async Task QuarantineAsync_ShouldRejectLegacyAnalysisBeforeMovingAFile()
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: true, evidenceVersion: 2);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None));

        Assert.True(fixture.FileSystem.FileExists(fixture.SourcePath));
        Assert.Equal(0, fixture.FileSystem.MoveCount);
    }

    [Fact]
    public async Task SaveAsync_ShouldRejectUnsupportedQuarantineProofBeforeReplacingCurrentManifest()
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: true);
        await fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None);
        QuarantineManifest manifest = Assert.Single(await fixture.ManifestStore.LoadAllAsync(CancellationToken.None));

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.ManifestStore.SaveAsync(manifest with { EvidenceVersion = 1 }, CancellationToken.None));

        QuarantineManifest actual = Assert.Single(await fixture.ManifestStore.LoadAllAsync(CancellationToken.None));
        Assert.Equal(FontDuplicateAnalyzer.CurrentEvidenceVersion, actual.EvidenceVersion);
        Assert.True(fixture.FileSystem.FileExists(Assert.Single(actual.Items).QuarantinePath));
    }
    [Theory]
    [InlineData(QuarantineItemStatus.Pending)]
    [InlineData(QuarantineItemStatus.Quarantined)]
    public async Task Startup_ShouldFenceMissingOldPathBeforeLoadingPublishedRuntimeIndex(QuarantineItemStatus durableStatus)
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: true);
        await fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None);
        QuarantineManifest manifest = Assert.Single(await fixture.ManifestStore.LoadAllAsync(CancellationToken.None));
        await fixture.ManifestStore.SaveAsync(manifest with
        {
            Items = [Assert.Single(manifest.Items) with { Status = durableStatus }]
        }, CancellationToken.None);
        var coordinator = new FontMaintenanceCoordinator();
        using var manager = Tests.Services.FontCacheManagerFixture.CreateManager(fixture.Repository, coordinator);
        var startup = new FontRuntimeSnapshotStartupService(
            manager,
            NullLogger<FontRuntimeSnapshotStartupService>.Instance,
            fixture.Repository,
            new FileRenameManifestStore(Path.Combine(Path.GetTempPath(), "rename-test-" + Guid.NewGuid().ToString("N"))),
            fixture.ManifestStore,
            coordinator);

        await startup.StartAsync(CancellationToken.None);

        Assert.False(coordinator.IsPathAvailable(fixture.SourcePath));
        Assert.NotEqual(fixture.SourcePath, (await Tests.Services.FontCacheManagerFixture.LookupAsync(manager, new FontDescriptor("Family", null, false, false)))?.Path);
    }

    [Theory]
    [InlineData("reconcile", 1, FontDuplicateAnalyzer.CurrentEvidenceVersion)]
    [InlineData("reconcile", 2, 1)]
    [InlineData("reconcile", 2, 2)]
    [InlineData("purge", 1, FontDuplicateAnalyzer.CurrentEvidenceVersion)]
    [InlineData("purge", 2, 1)]
    [InlineData("purge", 2, 2)]
    [InlineData("restore", 1, FontDuplicateAnalyzer.CurrentEvidenceVersion)]
    [InlineData("restore", 2, 1)]
    [InlineData("restore", 2, 2)]
    public async Task Operations_ShouldRejectUnsupportedPersistedManifestBeforeChangingFiles(string operation, int version, int evidenceVersion)
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: true);
        MaintenanceBatch batch = await fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None);
        QuarantineManifest manifest = Assert.Single(await fixture.ManifestStore.LoadAllAsync(CancellationToken.None));
        QuarantineManifestItem item = Assert.Single(manifest.Items);
        fixture.FileSystem.Add(fixture.SourcePath, [1, 2, 3, 4], DateTimeOffset.UnixEpoch);
        int previousMoves = fixture.FileSystem.MoveCount;
        fixture.TimeProvider.UtcNow = fixture.TimeProvider.UtcNow.AddDays(31);
        string root = Path.Combine(Path.GetTempPath(), nameof(FontQuarantineManagerTests), Guid.NewGuid().ToString("N"));
        try
        {
            string batchDirectory = Path.Combine(root, batch.Id.ToString("D20", System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(batchDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(batchDirectory, "manifest.json"),
                JsonSerializer.Serialize(manifest with { Version = version, EvidenceVersion = evidenceVersion }, JsonSerializerOptions.Web),
                TestContext.Current.CancellationToken);
            string quarantineRoot = Path.Combine(Path.GetTempPath(), "font-quarantine");
            var manager = new FontQuarantineManager(
                fixture.Repository,
                new FontMaintenanceCoordinator(),
                fixture.FileSystem,
                new FileQuarantineManifestStore(root, quarantineRoot),
                quarantineRoot,
                fixture.TimeProvider,
                () => new FontDirectoryPolicyResolver(Path.GetDirectoryName(fixture.SourcePath), null, null, false));
            OperationRequest restore = await new FontOperationRequestStore(fixture.Repository, TimeProvider.System)
                .CreateAsync(FontOperationKind.Restore, batch.Id, [item.FileId], "restore-unsupported-proof", CancellationToken.None);

            await Assert.ThrowsAsync<InvalidDataException>(async () =>
            {
                if (operation == "reconcile")
                {
                    await manager.ReconcileAsync(CancellationToken.None);
                }
                else if (operation == "purge")
                {
                    await manager.PurgeExpiredAsync(CancellationToken.None);
                }
                else
                {
                    await manager.RestoreAsync(restore, CancellationToken.None);
                }
            });

            Assert.Equal(previousMoves, fixture.FileSystem.MoveCount);
            Assert.True(fixture.FileSystem.FileExists(fixture.SourcePath));
            Assert.True(fixture.FileSystem.FileExists(item.QuarantinePath));
            Assert.True(fixture.FileSystem.FileExists(fixture.RetainedPath));
            Assert.Equal(item.Sha256.ToArray(), (await fixture.FileSystem.ReadEvidenceAsync(item.QuarantinePath, CancellationToken.None))!.Sha256.ToArray());
            Assert.Equal(QuarantineItemStatus.Quarantined, Assert.Single((await fixture.Repository.GetMaintenanceBatchAsync(batch.Id, CancellationToken.None))!.Items).Status);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
    [Fact]
    public async Task ReconcileAsync_ShouldRecheckIdentityAfterStagingPromotion()
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: false);
        fixture.FileSystem.ThrowAfterCopy = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None));
        fixture.FileSystem.ThrowAfterCopy = false;
        fixture.FileSystem.AfterMove = () => fixture.FileSystem.Identities[fixture.SourcePath] = "changed-during-promotion";

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.True(fixture.FileSystem.FileExists(fixture.SourcePath));
        Assert.Equal(QuarantineItemStatus.EvidenceDrift, Assert.Single((await fixture.Repository.GetMaintenanceBatchAsync(1, CancellationToken.None))!.Items).Status);
    }

    [Fact]
    public async Task RestoreAsync_ShouldNotWriteIntoNewlyProtectedSourceDirectory()
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: true);
        MaintenanceBatch quarantined = await fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None);
        OperationRequest request = await new FontOperationRequestStore(fixture.Repository, TimeProvider.System).CreateAsync(FontOperationKind.Restore, quarantined.Id, [2], "protected-restore", CancellationToken.None);
        FontQuarantineManager manager = fixture.CreateManager(protectedRoot: Path.GetDirectoryName(fixture.SourcePath));

        MaintenanceBatch result = await manager.RestoreAsync(request, CancellationToken.None);

        Assert.False(fixture.FileSystem.FileExists(fixture.SourcePath));
        Assert.Equal(QuarantineItemStatus.EvidenceDrift, Assert.Single(result.Items).Status);
        Assert.True(fixture.FileSystem.FileExists(Assert.Single(result.Items).QuarantinePath));
    }

    [Fact]
    public async Task ReconcileAsync_ShouldNotDeleteSameBytesWithChangedPhysicalIdentity()
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: false);
        fixture.FileSystem.AfterMove = () => throw new IOException("Interrupt after promoted copy.");
        await Assert.ThrowsAsync<IOException>(() => fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None));
        fixture.FileSystem.AfterMove = null;
        fixture.FileSystem.Identities[fixture.SourcePath] = "replacement-inode";

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.True(fixture.FileSystem.FileExists(fixture.SourcePath));
        Assert.Equal(QuarantineItemStatus.EvidenceDrift, Assert.Single((await fixture.Repository.GetMaintenanceBatchAsync(1, CancellationToken.None))!.Items).Status);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task QuarantineAsync_ShouldRejectCurrentIdentityOrHardLinkDrift(bool hardLink, bool beforeCopy)
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: beforeCopy);
        void Drift()
        {
            if (hardLink)
            {
                fixture.FileSystem.LinkCounts[fixture.SourcePath] = 2;
            }
            else
            {
                fixture.FileSystem.Identities[fixture.SourcePath] = "different-native-file";
            }
        }

        if (beforeCopy)
        {
            Drift();
        }
        else
        {
            fixture.FileSystem.AfterCopy = Drift;
        }

        MaintenanceBatch result = await fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None);

        Assert.True(fixture.FileSystem.FileExists(fixture.SourcePath));
        Assert.Equal(QuarantineItemStatus.EvidenceDrift, Assert.Single(result.Items).Status);
    }

    [Fact]
    public async Task QuarantineAsync_ShouldInvalidateSourceProtectedAfterAnalysis()
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: true);
        FontQuarantineManager currentManager = fixture.CreateManager(protectedRoot: Path.GetDirectoryName(fixture.SourcePath));

        MaintenanceBatch result = await currentManager.QuarantineAsync(fixture.Request, null, CancellationToken.None);

        Assert.True(fixture.FileSystem.FileExists(fixture.SourcePath));
        Assert.Equal(QuarantineItemStatus.EvidenceDrift, Assert.Single(result.Items).Status);
        Assert.Equal(0, fixture.FileSystem.MoveCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task QuarantineAsync_ShouldKeepSourceWhenItsPathBecomesLinked(bool beforeCopy)
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: beforeCopy);
        void RedirectSource() => fixture.FileSystem.UnsafeParents.Add(Path.GetDirectoryName(fixture.SourcePath)!);
        if (beforeCopy)
        {
            RedirectSource();
        }
        else
        {
            fixture.FileSystem.AfterCopy = RedirectSource;
        }

        MaintenanceBatch result = await fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None);

        Assert.True(fixture.FileSystem.FileExists(fixture.SourcePath));
        Assert.Equal(QuarantineItemStatus.EvidenceDrift, Assert.Single(result.Items).Status);
    }

    [Fact]
    public async Task QuarantineAsync_ShouldDeleteSourceOnlyAfterVerifiedCopy()
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: false);
        fixture.FileSystem.CorruptCopiedBytes = true;

        MaintenanceBatch result = await fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None);

        Assert.True(fixture.FileSystem.FileExists(fixture.SourcePath));
        QuarantineItemRecord item = Assert.Single(result.Items);
        Assert.False(fixture.FileSystem.FileExists(item.QuarantinePath));
        Assert.Equal(QuarantineItemStatus.FailedVerification, item.Status);
    }

    [Fact]
    public async Task QuarantineAsync_ShouldPersistStagedBeforePromotionAndVerifyFinalCopy()
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: false);
        bool stagedPersisted = false;
        fixture.FileSystem.AfterMove = () =>
        {
            QuarantineManifest manifest = Assert.Single(fixture.ManifestStore
                .LoadAllAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult());
            stagedPersisted = Assert.Single(manifest.Items).Status == QuarantineItemStatus.Staged;
        };

        MaintenanceBatch result = await fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None);

        Assert.True(stagedPersisted);
        Assert.Equal(QuarantineItemStatus.Quarantined, Assert.Single(result.Items).Status);
    }

    [Fact]
    public async Task QuarantineAsync_ShouldKeepSourceWhenPromotedCopyFailsVerification()
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: false);
        fixture.FileSystem.CorruptMovedBytes = true;

        MaintenanceBatch result = await fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None);

        Assert.Equal(QuarantineItemStatus.FailedVerification, Assert.Single(result.Items).Status);
        Assert.True(fixture.FileSystem.FileExists(fixture.SourcePath));
    }

    [Fact]
    public async Task QuarantineAsync_ShouldUseAtomicMoveOnSameVolume()
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: true);

        MaintenanceBatch result = await fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None);

        QuarantineItemRecord item = Assert.Single(result.Items);
        Assert.False(fixture.FileSystem.FileExists(fixture.SourcePath));
        Assert.True(fixture.FileSystem.FileExists(item.QuarantinePath));
        Assert.Equal(QuarantineItemStatus.Quarantined, item.Status);
        Assert.Equal(1, fixture.FileSystem.MoveCount);
        Assert.Equal(0, fixture.FileSystem.CopyCount);
        Assert.Contains(fixture.FileSystem.CreatedDirectories, path => Path.GetFileName(path) == "items");
    }

    [Fact]
    public async Task QuarantineAsync_WhenSourceRemovalIsDenied_ShouldRecordFailedItemAndContinue()
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: true, duplicateCount: 2);
        fixture.FileSystem.DeniedSourceRemovalPath = fixture.SourcePath;

        MaintenanceBatch result = await fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None);

        Assert.Equal(MaintenanceBatchStatus.Partial, result.Status);
        QuarantineItemRecord denied = Assert.Single(result.Items, item => item.FileId == 2);
        QuarantineItemRecord successful = Assert.Single(result.Items, item => item.FileId == 3);
        Assert.Equal(QuarantineItemStatus.FailedIo, denied.Status);
        Assert.Contains("permission", denied.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(fixture.FileSystem.FileExists(fixture.SourcePath));
        Assert.False(fixture.FileSystem.FileExists(denied.QuarantinePath));
        Assert.Equal(QuarantineItemStatus.Quarantined, successful.Status);
        Assert.False(fixture.FileSystem.FileExists(fixture.SecondSourcePath!));
        MaintenanceBatch persisted = Assert.IsType<MaintenanceBatch>(
            await fixture.Repository.GetMaintenanceBatchAsync(result.Id, CancellationToken.None));
        Assert.Equal(MaintenanceBatchStatus.Partial, persisted.Status);
    }

    [Fact]
    public async Task ReconcileAsync_WhenOldFailedMoveLeftCopy_ShouldKeepSourceAndCloseBatch()
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: true);
        fixture.FileSystem.DeniedSourceRemovalPath = fixture.SourcePath;
        MaintenanceBatch initial = await fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None);
        QuarantineItemRecord failed = Assert.Single(initial.Items);
        fixture.FileSystem.Add(failed.QuarantinePath, [1, 2, 3, 4], DateTimeOffset.UnixEpoch);
        await fixture.Repository.UpdateMaintenanceBatchAsync(
            initial with { Status = MaintenanceBatchStatus.Running, CompletedUtc = null },
            CancellationToken.None);

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        MaintenanceBatch persisted = Assert.IsType<MaintenanceBatch>(
            await fixture.Repository.GetMaintenanceBatchAsync(initial.Id, CancellationToken.None));
        QuarantineItemRecord recovered = Assert.Single(persisted.Items);
        Assert.Equal(MaintenanceBatchStatus.Partial, persisted.Status);
        Assert.Equal(QuarantineItemStatus.FailedIo, recovered.Status);
        Assert.True(fixture.FileSystem.FileExists(fixture.SourcePath));
        Assert.False(fixture.FileSystem.FileExists(failed.QuarantinePath));
    }

    [Fact]
    public async Task QuarantineAsync_ShouldReportCompletedFileCounts()
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: true, duplicateCount: 2);
        var percentages = new List<double>();

        await fixture.Manager.QuarantineAsync(
            fixture.Request,
            new RecordingProgress(percentages.Add),
            CancellationToken.None);

        Assert.Contains(50d, percentages);
        Assert.Equal(100d, percentages[^1]);
        Assert.Equal(percentages.OrderBy(value => value), percentages);
    }

    [Fact]
    public async Task QuarantineAsync_ShouldRejectInsufficientCrossVolumeSpaceBeforeCopy()
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: false);
        fixture.FileSystem.AvailableSpace = 0;

        await Assert.ThrowsAsync<IOException>(() => fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None));

        Assert.True(fixture.FileSystem.FileExists(fixture.SourcePath));
        Assert.Equal(0, fixture.FileSystem.CopyCount);
    }

    [Fact]
    public async Task QuarantineAsync_ShouldSkipChangedSourceEvidence()
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: true);
        fixture.FileSystem.Add(fixture.SourcePath, [9, 9, 9, 9], DateTimeOffset.UnixEpoch);

        MaintenanceBatch result = await fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None);

        Assert.Equal(QuarantineItemStatus.EvidenceDrift, Assert.Single(result.Items).Status);
        Assert.True(fixture.FileSystem.FileExists(fixture.SourcePath));
    }

    [Fact]
    public async Task QuarantineAsync_ShouldSkipWhenRetainedReplacementIsMissing()
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: true);
        await fixture.FileSystem.DeleteAsync(fixture.RetainedPath, CancellationToken.None);

        MaintenanceBatch result = await fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None);

        Assert.Equal(QuarantineItemStatus.ReplacementMissing, Assert.Single(result.Items).Status);
        Assert.True(fixture.FileSystem.FileExists(fixture.SourcePath));
    }

    [Fact]
    public async Task QuarantineAsync_ShouldKeepSourceWhenItChangesDuringCrossVolumeCopy()
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: false);
        fixture.FileSystem.AfterCopy = () => fixture.FileSystem.Add(
            fixture.SourcePath,
            [9, 9, 9, 9],
            DateTimeOffset.UnixEpoch.AddSeconds(1));

        MaintenanceBatch result = await fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None);

        Assert.Equal(QuarantineItemStatus.EvidenceDrift, Assert.Single(result.Items).Status);
        Assert.True(fixture.FileSystem.FileExists(fixture.SourcePath));
    }

    [Fact]
    public async Task QuarantineAsync_ShouldRejectCollectionWithUncoveredUniqueFace()
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(
            sameVolume: true,
            includeUncoveredFace: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manager.QuarantineAsync(
            fixture.Request,
            null,
            CancellationToken.None));

        Assert.True(fixture.FileSystem.FileExists(fixture.SourcePath));
    }

    [Fact]
    public async Task RestoreAsync_ShouldRefuseOccupiedOriginalPathWithoutOverwrite()
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: true);
        MaintenanceBatch quarantined = await fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None);
        fixture.FileSystem.Add(fixture.SourcePath, [7, 7, 7], DateTimeOffset.UnixEpoch);
        OperationRequest restoreRequest = await new FontOperationRequestStore(fixture.Repository, TimeProvider.System).CreateAsync(
            FontOperationKind.Restore,
            quarantined.Id,
            [2],
            "restore-request",
            CancellationToken.None);

        MaintenanceBatch result = await fixture.Manager.RestoreAsync(restoreRequest, CancellationToken.None);

        QuarantineItemRecord item = Assert.Single(result.Items);
        Assert.Equal(QuarantineItemStatus.DestinationOccupied, item.Status);
        Assert.True(fixture.FileSystem.FileExists(item.QuarantinePath));
        Assert.Equal(3, (await fixture.FileSystem.ReadEvidenceAsync(fixture.SourcePath, CancellationToken.None))!.Size);
    }

    [Fact]
    public async Task RestoreAsync_ShouldRejectPhysicalIdOutsideManifest()
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: true);
        MaintenanceBatch quarantined = await fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None);
        OperationRequest restoreRequest = await new FontOperationRequestStore(fixture.Repository, TimeProvider.System).CreateAsync(
            FontOperationKind.Restore,
            quarantined.Id,
            [999],
            "restore-unknown",
            CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manager.RestoreAsync(restoreRequest, CancellationToken.None));

        Assert.True(fixture.FileSystem.FileExists(Assert.Single(quarantined.Items).QuarantinePath));
    }

    [Fact]
    public async Task ReconcileAsync_ShouldFinishRestoreAfterSourcePromotionCrash()
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: false);
        MaintenanceBatch quarantined = await fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None);
        OperationRequest restoreRequest = await new FontOperationRequestStore(fixture.Repository, TimeProvider.System).CreateAsync(
            FontOperationKind.Restore,
            quarantined.Id,
            [2],
            "restore-crash",
            CancellationToken.None);
        fixture.FileSystem.AfterMove = () => throw new IOException("Simulated crash after restore promotion.");

        await Assert.ThrowsAsync<IOException>(() => fixture.Manager.RestoreAsync(restoreRequest, CancellationToken.None));
        fixture.FileSystem.AfterMove = null;
        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        QuarantineManifest manifest = Assert.Single(await fixture.ManifestStore.LoadAllAsync(CancellationToken.None));
        Assert.Equal(QuarantineItemStatus.Restored, Assert.Single(manifest.Items).Status);
        Assert.True(fixture.FileSystem.FileExists(fixture.SourcePath));
        Assert.False(fixture.FileSystem.FileExists(Assert.Single(quarantined.Items).QuarantinePath));
    }

    [Fact]
    public async Task PurgeExpiredAsync_ShouldEnforceFixedThirtyDayMinimum()
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: true);
        MaintenanceBatch quarantined = await fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None);
        QuarantineItemRecord item = Assert.Single(quarantined.Items);
        fixture.TimeProvider.UtcNow = fixture.TimeProvider.UtcNow.AddDays(29);

        int early = await fixture.Manager.PurgeExpiredAsync(CancellationToken.None);

        Assert.Equal(0, early);
        Assert.True(fixture.FileSystem.FileExists(item.QuarantinePath));
        fixture.TimeProvider.UtcNow = fixture.TimeProvider.UtcNow.AddDays(1);

        int expired = await fixture.Manager.PurgeExpiredAsync(CancellationToken.None);

        Assert.Equal(1, expired);
        Assert.False(fixture.FileSystem.FileExists(item.QuarantinePath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReconcileAsync_ShouldCompleteInterruptedRestoreIntent(bool sameVolume)
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume);
        MaintenanceBatch quarantined = await fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None);
        OperationRequest request = await new FontOperationRequestStore(fixture.Repository, TimeProvider.System).CreateAsync(
            FontOperationKind.Restore, quarantined.Id, [2], "restore-current-interrupted", CancellationToken.None);
        fixture.FileSystem.AfterMove = () => throw new IOException("Interrupted after restore promotion.");
        await Assert.ThrowsAsync<IOException>(() => fixture.Manager.RestoreAsync(request, CancellationToken.None));
        fixture.FileSystem.AfterMove = null;
        QuarantineManifest manifest = Assert.Single(await fixture.ManifestStore.LoadAllAsync(CancellationToken.None));
        QuarantineManifestItem item = Assert.Single(manifest.Items);
        Assert.Equal(QuarantineItemStatus.Restoring, item.Status);

        int reconciled = await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.Equal(1, reconciled);
        Assert.Equal(QuarantineItemStatus.Restored, Assert.Single(Assert.Single(await fixture.ManifestStore.LoadAllAsync(CancellationToken.None)).Items).Status);
        Assert.True(fixture.FileSystem.FileExists(item.SourcePath));
        Assert.False(fixture.FileSystem.FileExists(item.QuarantinePath));
        Assert.Equal(System.Security.Cryptography.SHA256.HashData([1, 2, 3, 4]), (await fixture.FileSystem.ReadEvidenceAsync(item.SourcePath, CancellationToken.None))!.Sha256.ToArray());
    }

    [Theory]
    [InlineData("source-protected", false)]
    [InlineData("source-protected", true)]
    [InlineData("source-unconfigured", false)]
    [InlineData("source-unconfigured", true)]
    [InlineData("quarantine-protected", false)]
    [InlineData("quarantine-protected", true)]
    [InlineData("source-redirected", false)]
    [InlineData("source-redirected", true)]
    [InlineData("quarantine-redirected", false)]
    [InlineData("quarantine-redirected", true)]
    [InlineData("replacement-redirected", false)]
    [InlineData("replacement-redirected", true)]
    public async Task PurgeExpiredAsync_ShouldKeepCopiesWhenCurrentPolicyOrPathsAreUnsafe(string drift, bool duringVerification)
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: true);
        MaintenanceBatch quarantined = await fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None);
        QuarantineItemRecord item = Assert.Single(quarantined.Items);
        fixture.TimeProvider.UtcNow = fixture.TimeProvider.UtcNow.AddDays(31);
        string? customRoot = Path.GetDirectoryName(fixture.SourcePath);
        string? protectedRoot = null;
        FontQuarantineManager manager = fixture.CreateManager(policy: () => new FontDirectoryPolicyResolver(customRoot, protectedRoot, null, false));
        void ChangeSafety()
        {
            switch (drift)
            {
                case "source-protected":
                    protectedRoot = Path.GetDirectoryName(fixture.SourcePath);
                    break;
                case "source-unconfigured":
                    customRoot = null;
                    break;
                case "quarantine-protected":
                    protectedRoot = Path.GetDirectoryName(item.QuarantinePath);
                    break;
                case "source-redirected":
                    fixture.FileSystem.UnsafeParents.Add(Path.GetDirectoryName(fixture.SourcePath)!);
                    break;
                case "quarantine-redirected":
                    fixture.FileSystem.UnsafeParents.Add(Path.GetDirectoryName(item.QuarantinePath)!);
                    break;
                case "replacement-redirected":
                    fixture.FileSystem.UnsafeParents.Add(Path.GetDirectoryName(fixture.RetainedPath)!);
                    break;
            }
        }

        if (duringVerification)
        {
            fixture.FileSystem.AfterEvidenceRead = path =>
            {
                if (path == item.QuarantinePath)
                {
                    ChangeSafety();
                }
            };
        }
        else
        {
            ChangeSafety();
        }

        int purged = await manager.PurgeExpiredAsync(CancellationToken.None);

        Assert.Equal(0, purged);
        Assert.True(fixture.FileSystem.FileExists(item.QuarantinePath));
        Assert.True(fixture.FileSystem.FileExists(fixture.RetainedPath));
        QuarantineItemRecord current = Assert.Single((await fixture.Repository.GetMaintenanceBatchAsync(quarantined.Id, CancellationToken.None))!.Items);
        Assert.Equal(QuarantineItemStatus.Quarantined, current.Status);
        Assert.False(string.IsNullOrWhiteSpace(current.Error));
        Assert.Equal(System.Security.Cryptography.SHA256.HashData([1, 2, 3, 4]), (await fixture.FileSystem.ReadEvidenceAsync(item.QuarantinePath, CancellationToken.None))!.Sha256.ToArray());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RestoreAsync_ShouldWorkAfterPurgeSafetyBlockIsRemoved(bool redirectQuarantine, bool duringVerification)
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: true);
        MaintenanceBatch quarantined = await fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None);
        QuarantineItemRecord item = Assert.Single(quarantined.Items);
        fixture.TimeProvider.UtcNow = fixture.TimeProvider.UtcNow.AddDays(31);
        string? protectedRoot = null;
        string quarantineParent = Path.GetDirectoryName(item.QuarantinePath)!;
        FontQuarantineManager manager = fixture.CreateManager(policy: () => new FontDirectoryPolicyResolver(
            Path.GetDirectoryName(fixture.SourcePath), protectedRoot, null, false));
        void BlockPurge()
        {
            if (redirectQuarantine)
            {
                fixture.FileSystem.UnsafeParents.Add(quarantineParent);
            }
            else
            {
                protectedRoot = Path.GetDirectoryName(fixture.SourcePath);
            }
        }

        if (duringVerification)
        {
            fixture.FileSystem.AfterEvidenceRead = path =>
            {
                if (path == item.QuarantinePath)
                {
                    BlockPurge();
                }
            };
        }
        else
        {
            BlockPurge();
        }

        Assert.Equal(0, await manager.PurgeExpiredAsync(CancellationToken.None));
        Assert.True(fixture.FileSystem.FileExists(item.QuarantinePath));
        fixture.FileSystem.AfterEvidenceRead = null;
        fixture.FileSystem.UnsafeParents.Remove(quarantineParent);
        protectedRoot = null;
        OperationRequest request = await new FontOperationRequestStore(fixture.Repository, TimeProvider.System).CreateAsync(
            FontOperationKind.Restore, quarantined.Id, [item.FileId], "restore-after-purge-safety-block", CancellationToken.None);

        MaintenanceBatch restored = await manager.RestoreAsync(request, CancellationToken.None);

        Assert.Equal(MaintenanceBatchStatus.Complete, restored.Status);
        Assert.Equal(QuarantineItemStatus.Restored, Assert.Single(restored.Items).Status);
        Assert.True(fixture.FileSystem.FileExists(item.SourcePath));
        Assert.False(fixture.FileSystem.FileExists(item.QuarantinePath));
        Assert.Equal(System.Security.Cryptography.SHA256.HashData([1, 2, 3, 4]), (await fixture.FileSystem.ReadEvidenceAsync(item.SourcePath, CancellationToken.None))!.Sha256.ToArray());
    }

    [Fact]
    public async Task PurgeExpiredAsync_ShouldExcludeRestoredItems()
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: true);
        MaintenanceBatch quarantined = await fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None);
        OperationRequest restoreRequest = await new FontOperationRequestStore(fixture.Repository, System.TimeProvider.System).CreateAsync(
            FontOperationKind.Restore,
            quarantined.Id,
            [2],
            "restore-success",
            CancellationToken.None);
        MaintenanceBatch restored = await fixture.Manager.RestoreAsync(restoreRequest, CancellationToken.None);
        fixture.TimeProvider.UtcNow = fixture.TimeProvider.UtcNow.AddDays(31);

        int purged = await fixture.Manager.PurgeExpiredAsync(CancellationToken.None);

        Assert.Equal(QuarantineItemStatus.Restored, Assert.Single(restored.Items).Status);
        Assert.Equal(0, purged);
        Assert.True(fixture.FileSystem.FileExists(fixture.SourcePath));
    }

    [Fact]
    public async Task PurgeExpiredAsync_ShouldKeepItemWhenReplacementDisappears()
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: true);
        MaintenanceBatch quarantined = await fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None);
        await fixture.FileSystem.DeleteAsync(fixture.RetainedPath, CancellationToken.None);
        fixture.TimeProvider.UtcNow = fixture.TimeProvider.UtcNow.AddDays(31);

        int purged = await fixture.Manager.PurgeExpiredAsync(CancellationToken.None);

        MaintenanceBatch current = (await fixture.Repository.GetMaintenanceBatchAsync(quarantined.Id, CancellationToken.None))!;
        QuarantineItemRecord item = Assert.Single(current.Items);
        Assert.Equal(0, purged);
        Assert.Equal(QuarantineItemStatus.ReplacementMissing, item.Status);
        Assert.True(fixture.FileSystem.FileExists(item.QuarantinePath));
    }

    [Fact]
    public async Task QuarantineAsync_ShouldPreserveExistingCopiesWhenCancelledWaitingForLease()
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: false);
        using var initialCancellation = new CancellationTokenSource();
        await fixture.Manager.QuarantineAsync(
            fixture.Request,
            new RecordingProgress(_ => initialCancellation.Cancel()),
            initialCancellation.Token);
        QuarantineManifestItem item = Assert.Single(Assert.Single(await fixture.ManifestStore.LoadAllAsync(CancellationToken.None)).Items);
        fixture.FileSystem.Add(item.StagingPath, [7, 8, 9], DateTimeOffset.UnixEpoch);
        fixture.FileSystem.Add(item.QuarantinePath, [1, 2, 3, 4], DateTimeOffset.UnixEpoch);
        var coordinator = new FontMaintenanceCoordinator();
        FontQuarantineManager manager = fixture.CreateManager(coordinator: coordinator);
        await using IAsyncDisposable heldLease = await coordinator.AcquireExclusiveAsync([item.SourcePath], CancellationToken.None);
        using var cancellation = new CancellationTokenSource();

        Task<MaintenanceBatch> pending = manager.QuarantineAsync(fixture.Request, null, cancellation.Token);
        Assert.False(pending.IsCompleted);
        cancellation.Cancel();
        MaintenanceBatch result = await pending;

        Assert.Equal(QuarantineItemStatus.Cancelled, Assert.Single(result.Items).Status);
        Assert.True(fixture.FileSystem.FileExists(item.StagingPath));
        Assert.True(fixture.FileSystem.FileExists(item.QuarantinePath));
        Assert.Equal(System.Security.Cryptography.SHA256.HashData([7, 8, 9]), (await fixture.FileSystem.ReadEvidenceAsync(item.StagingPath, CancellationToken.None))!.Sha256.ToArray());
        Assert.Equal(item.Sha256.ToArray(), (await fixture.FileSystem.ReadEvidenceAsync(item.QuarantinePath, CancellationToken.None))!.Sha256.ToArray());
        Assert.True(fixture.FileSystem.FileExists(item.SourcePath));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ReconcileAsync_ShouldPreserveExistingStagingAfterCancellation(bool quarantineCopyExists, bool stagingMatches)
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: false);
        using var initialCancellation = new CancellationTokenSource();
        await fixture.Manager.QuarantineAsync(
            fixture.Request,
            new RecordingProgress(_ => initialCancellation.Cancel()),
            initialCancellation.Token);
        QuarantineManifestItem item = Assert.Single(Assert.Single(await fixture.ManifestStore.LoadAllAsync(CancellationToken.None)).Items);
        byte[] stagingBytes = stagingMatches ? [1, 2, 3, 4] : [7, 8, 9];
        fixture.FileSystem.Add(item.StagingPath, stagingBytes, DateTimeOffset.UnixEpoch);
        if (quarantineCopyExists)
        {
            fixture.FileSystem.Add(item.QuarantinePath, [1, 2, 3, 4], DateTimeOffset.UnixEpoch);
        }

        var coordinator = new FontMaintenanceCoordinator();
        FontQuarantineManager manager = fixture.CreateManager(coordinator: coordinator);
        await using (IAsyncDisposable heldLease = await coordinator.AcquireExclusiveAsync([item.SourcePath], CancellationToken.None))
        {
            using var cancellation = new CancellationTokenSource();
            Task<MaintenanceBatch> pending = manager.QuarantineAsync(fixture.Request, null, cancellation.Token);
            Assert.False(pending.IsCompleted);
            cancellation.Cancel();
            Assert.Equal(QuarantineItemStatus.Cancelled, Assert.Single((await pending).Items).Status);
        }

        fixture.FileSystem.Add(item.SourcePath, [9, 9, 9, 9], DateTimeOffset.UnixEpoch);
        await manager.ReconcileAsync(CancellationToken.None);
        Assert.Equal(QuarantineItemStatus.Cancelled, Assert.Single(Assert.Single(await fixture.ManifestStore.LoadAllAsync(CancellationToken.None)).Items).Status);
        Assert.True(fixture.FileSystem.FileExists(item.StagingPath));
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(stagingBytes), (await fixture.FileSystem.ReadEvidenceAsync(item.StagingPath, CancellationToken.None))!.Sha256.ToArray());
        fixture.FileSystem.Add(item.SourcePath, [1, 2, 3, 4], DateTimeOffset.UnixEpoch);
        fixture.FileSystem.Add(item.StagingPath, [1, 2, 3, 4], DateTimeOffset.UnixEpoch);
        await manager.ReconcileAsync(CancellationToken.None);
        await manager.ReconcileAsync(CancellationToken.None);

        QuarantineManifestItem reconciled = Assert.Single(Assert.Single(await fixture.ManifestStore.LoadAllAsync(CancellationToken.None)).Items);
        Assert.Equal(QuarantineItemStatus.Cancelled, reconciled.Status);
        Assert.NotNull(reconciled.Error);
        Assert.True(fixture.FileSystem.FileExists(item.SourcePath));
        Assert.Equal(System.Security.Cryptography.SHA256.HashData([1, 2, 3, 4]), (await fixture.FileSystem.ReadEvidenceAsync(item.SourcePath, CancellationToken.None))!.Sha256.ToArray());
        Assert.True(fixture.FileSystem.FileExists(item.StagingPath));
        Assert.Equal(System.Security.Cryptography.SHA256.HashData([1, 2, 3, 4]), (await fixture.FileSystem.ReadEvidenceAsync(item.StagingPath, CancellationToken.None))!.Sha256.ToArray());
        Assert.Equal(quarantineCopyExists, fixture.FileSystem.FileExists(item.QuarantinePath));
        if (quarantineCopyExists)
        {
            Assert.Equal(System.Security.Cryptography.SHA256.HashData([1, 2, 3, 4]), (await fixture.FileSystem.ReadEvidenceAsync(item.QuarantinePath, CancellationToken.None))!.Sha256.ToArray());
        }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task QuarantineAsync_ShouldRestoreAvailabilityOnlyForUnchangedSourceAfterCancelledMutation(bool sameVolume, bool sourceChanged)
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume);
        var coordinator = new FontMaintenanceCoordinator();
        FontQuarantineManager manager = fixture.CreateManager(coordinator: coordinator);
        using var cancellation = new CancellationTokenSource();
        fixture.FileSystem.BeforeMutation = path =>
        {
            if (!string.Equals(path, fixture.SourcePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Assert.False(coordinator.IsPathAvailable(path));
            if (sourceChanged)
            {
                fixture.FileSystem.Add(path, [9, 9, 9, 9], DateTimeOffset.UnixEpoch);
            }

            cancellation.Cancel();
        };

        MaintenanceBatch result = await manager.QuarantineAsync(fixture.Request, null, cancellation.Token);

        Assert.Equal(QuarantineItemStatus.Cancelled, Assert.Single(result.Items).Status);
        Assert.True(fixture.FileSystem.FileExists(fixture.SourcePath));
        byte[] expectedBytes = sourceChanged ? [9, 9, 9, 9] : [1, 2, 3, 4];
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(expectedBytes), (await fixture.FileSystem.ReadEvidenceAsync(fixture.SourcePath, CancellationToken.None))!.Sha256.ToArray());
        Assert.Equal(!sourceChanged, coordinator.IsPathAvailable(fixture.SourcePath));
    }

    [Fact]
    public async Task QuarantineAsync_ShouldPreserveVerifiedPromotedCopyWhenCancellationFindsCorruptSource()
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: false);
        using var cancellation = new CancellationTokenSource();
        var progress = new RecordingProgress(value =>
        {
            if (value == 80d)
            {
                fixture.FileSystem.Add(fixture.SourcePath, [9, 9, 9, 9], DateTimeOffset.UnixEpoch);
                cancellation.Cancel();
            }
        });

        MaintenanceBatch result = await fixture.Manager.QuarantineAsync(fixture.Request, progress, cancellation.Token);

        QuarantineItemRecord item = Assert.Single(result.Items);
        Assert.Equal(QuarantineItemStatus.Cancelled, item.Status);
        Assert.True(fixture.FileSystem.FileExists(item.QuarantinePath));
        Assert.Equal(System.Security.Cryptography.SHA256.HashData([1, 2, 3, 4]), (await fixture.FileSystem.ReadEvidenceAsync(item.QuarantinePath, CancellationToken.None))!.Sha256.ToArray());
        Assert.Equal(System.Security.Cryptography.SHA256.HashData([9, 9, 9, 9]), (await fixture.FileSystem.ReadEvidenceAsync(fixture.SourcePath, CancellationToken.None))!.Sha256.ToArray());
    }

    [Fact]
    public async Task QuarantineAsync_ShouldStopBeforeLaterItemsAndResumeSameBatchAfterCancellation()
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: true, duplicateCount: 2);
        using var cancellation = new CancellationTokenSource();
        fixture.FileSystem.AfterMove = cancellation.Cancel;

        MaintenanceBatch cancelled = await fixture.Manager.QuarantineAsync(fixture.Request, null, cancellation.Token);

        Assert.Equal(MaintenanceBatchStatus.Cancelled, cancelled.Status);
        Assert.False(fixture.FileSystem.FileExists(fixture.SourcePath));
        Assert.True(fixture.FileSystem.FileExists(fixture.SecondSourcePath!));
        fixture.FileSystem.AfterMove = null;

        MaintenanceBatch resumed = await fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None);

        Assert.Equal(cancelled.Id, resumed.Id);
        Assert.Equal(MaintenanceBatchStatus.Complete, resumed.Status);
        Assert.All(resumed.Items, item => Assert.Equal(QuarantineItemStatus.Quarantined, item.Status));
        Assert.False(fixture.FileSystem.FileExists(fixture.SecondSourcePath!));
    }

    [Fact]
    public async Task QuarantineAsync_ShouldReuseBatchWhenInitialManifestWasNotPersisted()
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: true);
        MaintenanceBatch interrupted = await fixture.Repository.SaveMaintenanceBatchAsync(
            new MaintenanceBatch
            {
                OperationRequestId = fixture.Request.Id,
                Kind = FontOperationKind.Quarantine,
                Status = MaintenanceBatchStatus.Running,
                StartedUtc = fixture.TimeProvider.UtcNow,
                SummaryJson = "{}"
            },
            CancellationToken.None);

        MaintenanceBatch result = await fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None);

        Assert.Equal(interrupted.Id, result.Id);
        Assert.Equal(QuarantineItemStatus.Quarantined, Assert.Single(result.Items).Status);
    }

    [Fact]
    public async Task ReconcileAsync_ShouldFinishVerifiedStagingCopyAfterInterruption()
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: false);
        fixture.FileSystem.ThrowAfterCopy = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None));
        fixture.FileSystem.ThrowAfterCopy = false;

        int reconciled = await fixture.Manager.ReconcileAsync(CancellationToken.None);

        QuarantineManifest manifest = Assert.Single(await fixture.ManifestStore.LoadAllAsync(CancellationToken.None));
        QuarantineManifestItem item = Assert.Single(manifest.Items);
        Assert.Equal(1, reconciled);
        Assert.Equal(QuarantineItemStatus.Quarantined, item.Status);
        Assert.False(fixture.FileSystem.FileExists(fixture.SourcePath));
        Assert.True(fixture.FileSystem.FileExists(item.QuarantinePath));
    }

    [Fact]
    public async Task ReconcileAsync_ShouldImportButNotAdvanceWhenSourceBecomesProtected()
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: false);
        fixture.FileSystem.ThrowAfterCopy = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None));
        fixture.FileSystem.ThrowAfterCopy = false;
        var rebuilt = new InMemoryFontRepository();
        var manager = fixture.CreateManager(rebuilt, protectedRoot: Path.GetDirectoryName(fixture.SourcePath));

        Assert.Equal(0, await manager.ReconcileAsync(CancellationToken.None));

        Assert.NotNull(await rebuilt.GetMaintenanceBatchAsync(1, CancellationToken.None));
        Assert.True(fixture.FileSystem.FileExists(fixture.SourcePath));
        Assert.True(fixture.FileSystem.FileExists(Assert.Single(await fixture.ManifestStore.LoadAllAsync(CancellationToken.None)).Items[0].StagingPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReconcileAsync_ShouldNotMutateThroughRedirectedSourceOrStagingParent(bool redirectStaging)
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: false);
        fixture.FileSystem.ThrowAfterCopy = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None));
        fixture.FileSystem.ThrowAfterCopy = false;
        QuarantineManifestItem item = Assert.Single(Assert.Single(await fixture.ManifestStore.LoadAllAsync(CancellationToken.None)).Items);
        fixture.FileSystem.UnsafeParents.Add(Path.GetDirectoryName(redirectStaging ? item.StagingPath : item.SourcePath)!);

        Assert.Equal(0, await fixture.Manager.ReconcileAsync(CancellationToken.None));
        Assert.True(fixture.FileSystem.FileExists(fixture.SourcePath));
        Assert.True(fixture.FileSystem.FileExists(item.StagingPath));
    }

    [Fact]
    public async Task ReconcileAsync_ShouldRecheckPathSafetyImmediatelyBeforeSourceDeletion()
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: false);
        fixture.FileSystem.ThrowAfterCopy = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None));
        fixture.FileSystem.ThrowAfterCopy = false;
        fixture.FileSystem.AfterMove = () => fixture.FileSystem.UnsafeParents.Add(Path.GetDirectoryName(fixture.SourcePath)!);

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.True(fixture.FileSystem.FileExists(fixture.SourcePath));
        Assert.Equal(QuarantineItemStatus.EvidenceDrift, Assert.Single(Assert.Single(await fixture.ManifestStore.LoadAllAsync(CancellationToken.None)).Items).Status);
    }

    [Fact]
    public async Task ReconcileAsync_ShouldKeepSourceWhenFinalPromotionFailsVerification()
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: false);
        fixture.FileSystem.ThrowAfterCopy = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None));
        fixture.FileSystem.ThrowAfterCopy = false;
        fixture.FileSystem.CorruptMovedBytes = true;

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        QuarantineManifest manifest = Assert.Single(await fixture.ManifestStore.LoadAllAsync(CancellationToken.None));
        Assert.Equal(QuarantineItemStatus.FailedVerification, Assert.Single(manifest.Items).Status);
        Assert.True(fixture.FileSystem.FileExists(fixture.SourcePath));
    }

    [Fact]
    public async Task ReconcileAsync_ShouldRevalidateSourceAfterPromotionBeforeDeletion()
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: false);
        fixture.FileSystem.ThrowAfterCopy = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None));
        fixture.FileSystem.ThrowAfterCopy = false;
        fixture.FileSystem.AfterMove = () => fixture.FileSystem.Add(
            fixture.SourcePath,
            [9, 9, 9, 9],
            DateTimeOffset.UnixEpoch.AddSeconds(1));

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        QuarantineManifest manifest = Assert.Single(await fixture.ManifestStore.LoadAllAsync(CancellationToken.None));
        Assert.Equal(QuarantineItemStatus.EvidenceDrift, Assert.Single(manifest.Items).Status);
        Assert.True(fixture.FileSystem.FileExists(fixture.SourcePath));
    }

    [Fact]
    public async Task ReconcileAsync_ShouldResumeWhenCrashPrecedesPromotedPersistence()
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: false);
        fixture.FileSystem.ThrowAfterCopy = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None));
        fixture.FileSystem.ThrowAfterCopy = false;
        fixture.FileSystem.AfterMove = () => throw new IOException("Simulated crash before promoted persistence.");
        await Assert.ThrowsAsync<IOException>(() => fixture.Manager.ReconcileAsync(CancellationToken.None));
        fixture.FileSystem.AfterMove = null;

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        QuarantineManifest manifest = Assert.Single(await fixture.ManifestStore.LoadAllAsync(CancellationToken.None));
        Assert.Equal(QuarantineItemStatus.Quarantined, Assert.Single(manifest.Items).Status);
        Assert.False(fixture.FileSystem.FileExists(fixture.SourcePath));
    }

    [Fact]
    public async Task ReconcileAsync_ShouldNotDeleteRecreatedSourceForCompletedQuarantine()
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: true);
        await fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None);
        fixture.FileSystem.Add(fixture.SourcePath, [1, 2, 3, 4], DateTimeOffset.UnixEpoch);

        await fixture.Manager.ReconcileAsync(CancellationToken.None);

        Assert.True(fixture.FileSystem.FileExists(fixture.SourcePath));
    }

    [Fact]
    public async Task ReconcileAsync_ShouldImportManifestAfterDatabaseRebuild()
    {
        QuarantineFixture fixture = await QuarantineFixture.CreateAsync(sameVolume: true);
        MaintenanceBatch original = await fixture.Manager.QuarantineAsync(fixture.Request, null, CancellationToken.None);
        var rebuiltRepository = new InMemoryFontRepository();
        var rebuiltManager = new FontQuarantineManager(
            rebuiltRepository,
            new FontMaintenanceCoordinator(),
            fixture.FileSystem,
            fixture.ManifestStore,
            Path.Combine(Path.GetTempPath(), "font-quarantine"),
            fixture.TimeProvider,
            () => new FontDirectoryPolicyResolver(Path.GetDirectoryName(fixture.SourcePath), null, null, false));

        await rebuiltManager.ReconcileAsync(CancellationToken.None);

        MaintenanceBatch? imported = await rebuiltRepository.GetMaintenanceBatchAsync(original.Id, CancellationToken.None);
        Assert.NotNull(imported);
        Assert.Equal(original.OperationRequestId, imported.OperationRequestId);
        Assert.Equal(QuarantineItemStatus.Quarantined, Assert.Single(imported.Items).Status);
    }

    private sealed class QuarantineFixture
    {
        private QuarantineFixture(
            FontQuarantineManager manager,
            InMemoryFontRepository repository,
            FakeQuarantineFileSystem fileSystem,
            InMemoryQuarantineManifestStore manifestStore,
            OperationRequest request,
            string sourcePath,
            string? secondSourcePath,
            string retainedPath,
            AdjustableTimeProvider timeProvider)
        {
            Manager = manager;
            Repository = repository;
            FileSystem = fileSystem;
            ManifestStore = manifestStore;
            Request = request;
            SourcePath = sourcePath;
            SecondSourcePath = secondSourcePath;
            RetainedPath = retainedPath;
            TimeProvider = timeProvider;
        }

        public FontQuarantineManager Manager { get; }

        public InMemoryFontRepository Repository { get; }

        public FakeQuarantineFileSystem FileSystem { get; }

        public InMemoryQuarantineManifestStore ManifestStore { get; }

        public OperationRequest Request { get; }

        public string SourcePath { get; }

        public string? SecondSourcePath { get; }

        public string RetainedPath { get; }

        public AdjustableTimeProvider TimeProvider { get; }

        public FontQuarantineManager CreateManager(
            IFontRepository? repository = null,
            string? protectedRoot = null,
            FontMaintenanceCoordinator? coordinator = null,
            Func<FontDirectoryPolicyResolver>? policy = null)
            => new(
                repository ?? Repository,
                coordinator ?? new FontMaintenanceCoordinator(),
                FileSystem,
                ManifestStore,
                Path.Combine(Path.GetTempPath(), "font-quarantine"),
                TimeProvider,
                policy ?? (() => new FontDirectoryPolicyResolver(Path.GetDirectoryName(SourcePath), protectedRoot, null, false)));

        public static async Task<QuarantineFixture> CreateAsync(
            bool sameVolume,
            int duplicateCount = 1,
            bool includeUncoveredFace = false,
            int evidenceVersion = FontDuplicateAnalyzer.CurrentEvidenceVersion)
        {
            var repository = new InMemoryFontRepository();
            long generationId = await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
            string sourcePath = Path.Combine(Path.GetTempPath(), "font-source", "duplicate.ttf");
            string secondSourcePath = Path.Combine(Path.GetTempPath(), "font-source", "duplicate-2.ttf");
            string retainedPath = Path.Combine(Path.GetTempPath(), "font-retained", "retained.ttf");
            byte[] duplicateBytes = [1, 2, 3, 4];
            byte[] retainedBytes = [5, 6, 7, 8];
            FontFileRecord retained = File(1, generationId, retainedPath, retainedBytes, 11);
            FontFileRecord duplicate = File(2, generationId, sourcePath, duplicateBytes, 12);
            if (includeUncoveredFace)
            {
                FontFaceRecord firstFace = Assert.Single(duplicate.Faces);
                duplicate = duplicate with
                {
                    Faces = [firstFace, firstFace with { Id = 14, FaceIndex = 1 }]
                };
            }

            FontFileRecord secondDuplicate = File(3, generationId, secondSourcePath, duplicateBytes, 13);
            FontFileRecord[] inventory = duplicateCount == 1 ? [retained, duplicate] : [retained, duplicate, secondDuplicate];
            await repository.WriteInventoryAsync(generationId, inventory, CancellationToken.None);

            AnalysisBatch analysis = await repository.PublishAnalysisAsync(
                new AnalysisBatch
                {
                    GenerationId = generationId,
                    Status = AnalysisBatchStatus.Complete,
                    EvidenceVersion = evidenceVersion,
                    StartedUtc = DateTimeOffset.UnixEpoch,
                    CompletedUtc = DateTimeOffset.UnixEpoch,
                    SummaryJson = "{}",
                    Relations = inventory.Skip(1).Select(candidate => new FontRelationRecord
                    {
                        LeftFaceId = 11,
                        RightFaceId = candidate.Faces[0].Id,
                        RelationshipKind = FontRelationshipKind.StrictFaceDuplicate,
                        ReplacementDirection = FontReplacementDirection.LeftReplacesRight,
                        EvidenceVersion = evidenceVersion,
                        EvidenceJson = "{}"
                    }).ToArray(),
                    Groups = inventory.Skip(1).Select(candidate =>
                        new DuplicateGroupRecord
                        {
                            RetainedFaceId = 11,
                            EvidenceJson = "{}",
                            ReclaimableBytes = candidate.Size,
                            Members =
                            [
                                Member(11, 1, DuplicateGroupMemberDecision.Retained),
                                Member(candidate.Faces[0].Id, candidate.Id, DuplicateGroupMemberDecision.QuarantineCandidate)
                            ]
                        }).ToArray()
                },
                CancellationToken.None);
            var requestStore = new FontOperationRequestStore(repository, System.TimeProvider.System);
            OperationRequest request = await requestStore.CreateAsync(
                FontOperationKind.Quarantine,
                analysis.Id,
                inventory.Skip(1).Select(file => file.Id).ToArray(),
                "quarantine-request",
                CancellationToken.None);
            var fileSystem = new FakeQuarantineFileSystem(sameVolume);
            fileSystem.Add(sourcePath, duplicateBytes, duplicate.ModifiedUtc);
            if (duplicateCount > 1)
            {
                fileSystem.Add(secondSourcePath, duplicateBytes, secondDuplicate.ModifiedUtc);
            }
            fileSystem.Add(retainedPath, retainedBytes, retained.ModifiedUtc);
            string quarantineRoot = Path.Combine(Path.GetTempPath(), "font-quarantine");
            var timeProvider = new AdjustableTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
            var manifestStore = new InMemoryQuarantineManifestStore();
            var manager = new FontQuarantineManager(
                repository,
                new FontMaintenanceCoordinator(),
                fileSystem,
                manifestStore,
                quarantineRoot,
                timeProvider,
                () => new FontDirectoryPolicyResolver(Path.GetDirectoryName(sourcePath), null, null, false));
            return new QuarantineFixture(
                manager,
                repository,
                fileSystem,
                manifestStore,
                request,
                sourcePath,
                duplicateCount > 1 ? secondSourcePath : null,
                retainedPath,
                timeProvider);
        }

        private static FontFileRecord File(
            long fileId,
            long generationId,
            string path,
            byte[] bytes,
            long faceId)
        {
            return new FontFileRecord
            {
                Id = fileId,
                GenerationId = generationId,
                Path = path,
                NormalizedPath = path.ToUpperInvariant(),
                PhysicalIdentity = path,
                Size = bytes.Length,
                ModifiedUtc = DateTimeOffset.UnixEpoch,
                Sha256 = System.Security.Cryptography.SHA256.HashData(bytes),
                Health = FontFileHealth.Healthy,
                Faces =
                [
                    new FontFaceRecord
                    {
                        Id = faceId,
                        FaceIndex = 0,
                        StrictFingerprint = new byte[32],
                        FamilyName = "Family",
                        StyleName = "Regular",
                        Names = ["Family"],
                        Technology = "TrueType"
                    }
                ]
            };
        }

        private static DuplicateGroupMemberRecord Member(
            long faceId,
            long fileId,
            DuplicateGroupMemberDecision decision)
        {
            return new DuplicateGroupMemberRecord
            {
                FaceId = faceId,
                FileId = fileId,
                Decision = decision,
                ScoreJson = "{}",
                Reason = decision.ToString()
            };
        }
    }

    private sealed class FakeQuarantineFileSystem(bool sameVolume) : IQuarantineFileSystem
    {
        private readonly Dictionary<string, (byte[] Bytes, DateTimeOffset ModifiedUtc)> _files = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> Identities { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, uint> LinkCounts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool CorruptCopiedBytes { get; set; }

        public bool CorruptMovedBytes { get; set; }

        public long AvailableSpace { get; set; } = long.MaxValue;

        public int CopyCount { get; private set; }

        public int MoveCount { get; private set; }

        public List<string> CreatedDirectories { get; } = [];

        public HashSet<string> UnsafeParents { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool IsPhysicalPath(string path) => !UnsafeParents.Any(parent => Path.GetFullPath(path).StartsWith(Path.GetFullPath(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

        public Action<string>? BeforeMutation { get; set; }

        public Action? AfterMove { get; set; }

        public string? DeniedSourceRemovalPath { get; set; }

        public bool ThrowAfterCopy { get; set; }

        public Action? AfterCopy { get; set; }

        public Action<string>? AfterEvidenceRead { get; set; }

        public void Add(string path, byte[] bytes, DateTimeOffset modifiedUtc)
            => _files[path] = (bytes, modifiedUtc);

        public bool FileExists(string path) => _files.ContainsKey(path);

        public bool IsSameVolume(string sourcePath, string destinationPath) => sameVolume;

        public Task<long> GetAvailableSpaceAsync(string destinationPath, CancellationToken cancellationToken)
            => Task.FromResult(AvailableSpace);

        public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreatedDirectories.Add(path);
            return Task.CompletedTask;
        }

        public Task<QuarantineFileEvidence?> ReadEvidenceAsync(string path, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_files.TryGetValue(path, out var file))
            {
                return Task.FromResult<QuarantineFileEvidence?>(null);
            }

            var evidence = new QuarantineFileEvidence(
                file.Bytes.Length,
                file.ModifiedUtc,
                System.Security.Cryptography.SHA256.HashData(file.Bytes),
                Identities.GetValueOrDefault(path, path),
                LinkCounts.GetValueOrDefault(path, 1U));
            AfterEvidenceRead?.Invoke(path);
            return Task.FromResult<QuarantineFileEvidence?>(evidence);
        }

        public Task CopyAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
        {
            CopyCount++;
            var source = _files[sourcePath];
            byte[] bytes = source.Bytes.ToArray();
            if (CorruptCopiedBytes)
            {
                bytes[0] ^= 0xff;
            }

            _files.Add(destinationPath, (bytes, source.ModifiedUtc));
            AfterCopy?.Invoke();
            if (ThrowAfterCopy)
            {
                throw new IOException("Simulated interruption after copy.");
            }

            return Task.CompletedTask;
        }

        public Task MoveAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
        {
            BeforeMutation?.Invoke(sourcePath);
            cancellationToken.ThrowIfCancellationRequested();
            MoveCount++;
            _files.Add(destinationPath, _files[sourcePath]);
            if (string.Equals(sourcePath, DeniedSourceRemovalPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException("Permission denied while removing the source.");
            }

            _files.Remove(sourcePath);
            if (CorruptMovedBytes)
            {
                (byte[] bytes, DateTimeOffset modifiedUtc) = _files[destinationPath];
                bytes = bytes.ToArray();
                bytes[0] ^= 0xff;
                _files[destinationPath] = (bytes, modifiedUtc);
            }

            AfterMove?.Invoke();
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string path, CancellationToken cancellationToken)
        {
            BeforeMutation?.Invoke(path);
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(path, DeniedSourceRemovalPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException("Permission denied while removing the source.");
            }

            _files.Remove(path);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingProgress(Action<double> record) : IProgress<double>
    {
        public void Report(double value) => record(value);
    }

    private sealed class AdjustableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
