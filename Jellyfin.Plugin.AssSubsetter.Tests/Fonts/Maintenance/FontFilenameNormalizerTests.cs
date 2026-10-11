using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance.Naming;
using Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Persistence;
using Jellyfin.Plugin.AssSubsetter.Services;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Cryptography;
using System.Runtime.InteropServices;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Maintenance;

public class FontFilenameNormalizerTests
{
    [Fact]
    public async Task ExecuteAsyncReportsOccupiedAndRenamedFilesBeforeFinalization()
    {
        await using var fixture = await RenameFixture.CreateAsync();
        await fixture.PublishAsync([await fixture.AddAsync(1, "a.ttf"), await fixture.AddAsync(2, "b.ttf")]);
        await fixture.SaveAnalysisAsync();
        await fixture.Candidates.GetCurrentAsync(CancellationToken.None);
        OperationRequest request = (await fixture.Normalizer.CreateCurrentRequestAsync(CancellationToken.None))!;
        var selected = FontOperationRequestPayloadCodec.ParseRenameExecution(request.RequestJson).Items;
        await System.IO.File.WriteAllTextAsync(selected[0].NewPath, "occupied", TestContext.Current.CancellationToken);
        var reports = new List<double>();

        MaintenanceBatch batch = await fixture.Normalizer.ExecuteAsync(request, CancellationToken.None, new Jellyfin.Plugin.AssSubsetter.Tests.ScheduledTasks.InlineProgress(reports.Add));

        Assert.Equal(MaintenanceBatchStatus.Partial, batch.Status);
        Assert.Single(batch.RenameItems, item => item.Status == RenameItemStatus.DestinationOccupied);
        Assert.Single(batch.RenameItems, item => item.Status == RenameItemStatus.Renamed);
        Assert.Contains(47.5d, reports);
        Assert.Contains(90d, reports);
        Assert.Equal(100d, reports[^1]);
        Assert.Equal(reports.OrderBy(value => value), reports);
    }

    [Fact]
    public void ResolveCollisions_ShouldExcludeOnlyFingerprintThatCannotFitAfterVersion()
    {
        string parent = LongParent(218);
        FontFileRecord[] files =
        [
            File(1, "a.ttf", Face("Example", "Regular")) with { Path = Path.Combine(parent, "a.ttf") },
            File(2, "b.ttf", Face("Example", "Regular") with { StrictFingerprint = Convert.FromHexString("a1b2c3d4".PadRight(64, '0')) }) with { Path = Path.Combine(parent, "b.ttf") },
            File(3, Path.Combine("short", "other.ttf"), Face("Other", "Regular"))
        ];

        IReadOnlyDictionary<long, string?> names = Resolve(files);

        Assert.Equal("Ex-Regular-v2.003.ttf", names[1]);
        Assert.Null(names[2]);
        Assert.Equal("Other-Regular-v2.003.ttf", names[3]);
    }

    [Fact]
    public void ResolveCollisions_ShouldExcludeOnlyFinalFingerprintThatCannotFit()
    {
        string parent = LongParent(219);
        FontFileRecord[] files =
        [
            File(1, "a.ttf", Face("ExampleA", "Regular")) with { Path = Path.Combine(parent, "a.ttf") },
            File(2, "b.ttf", Face("ExampleB", "Regular") with { StrictFingerprint = Convert.FromHexString("a1b2c3d4".PadRight(64, '0')) }) with { Path = Path.Combine(parent, "b.ttf") },
            File(3, Path.Combine("short", "other.ttf"), Face("Other", "Regular"))
        ];

        IReadOnlyDictionary<long, string?> names = Resolve(files);

        Assert.NotNull(names[1]);
        Assert.Null(names[2]);
        Assert.Equal("Other-Regular-v2.003.ttf", names[3]);
    }

    [Theory]
    [InlineData(210, "a-Regular-v2.003-22222222.ttf", "2.003", "22222222")]
    [InlineData(211, null, null, null)]
    public void ResolveCollisionState_ShouldKeepCompleteFinalSuffixOrExcludeOnlyItem(int parentLength, string? expectedName, string? expectedVersion, string? expectedFingerprint)
    {
        string parent = LongParent(parentLength);
        FontFileRecord[] files =
        [
            File(1, "a1.ttf", Face("abcdefghij-A", "Regular")) with { Path = Path.Combine(parent, "a1.ttf") },
            File(2, "a2.ttf", Face("abcdefghij-A", "Regular") with { StrictFingerprint = Convert.FromHexString("11111111".PadRight(64, '0')) }) with { Path = Path.Combine(parent, "a2.ttf") },
            File(3, "b1.ttf", Face("abcdefghij-B", "Regular") with { StrictFingerprint = Convert.FromHexString("22222222".PadRight(64, '0')) }) with { Path = Path.Combine(parent, "b1.ttf") },
            File(4, "b2.ttf", Face("abcdefghij-B", "Regular") with { StrictFingerprint = Convert.FromHexString("33333333".PadRight(64, '0')) }) with { Path = Path.Combine(parent, "b2.ttf") },
            File(5, "peer.ttf", Face("Peer", "Regular"))
        ];

        var resolution = ResolveState(files);

        Assert.Equal(expectedName, resolution.Names[3]);
        Assert.Equal((expectedVersion, expectedFingerprint), resolution.Provenance[3]);
        Assert.Equal("Peer-Regular-v2.003.ttf", resolution.Names[5]);
    }

    [Fact]
    public void ResolveCollisions_ShouldDisambiguateAfterPortableTruncation()
    {
        FontFileRecord[] files = [File(1, "a.ttf", Face(new string('a', 250) + "b", "Regular")), File(2, "b.ttf", Face(new string('a', 250) + "c", "Regular"))];
        var names = Resolve(files);
        Assert.NotEqual(names[1], names[2]);
        Assert.EndsWith("-Regular-v2.003-00112233.ttf", names[2], StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSingleFaceName_ShouldKeepIdentityHyphensAndUseCompleteFallbackPairs()
    {
        FontFaceRecord face = Face("Legacy", "Regular") with { NameRecords = [Name(1, "Legacy"), Name(2, "Regular"), Name(16, "Incomplete"), Name(6, "Example-Sans")] };
        Assert.Equal("Legacy-Regular-v2.003.ttf", DefaultName(face, ".ttf"));
        Assert.Equal("Legacy-Regular-v2.003.ttf", DefaultName(face with { NameRecords = face.NameRecords.Where(name => name.NameId != 6).ToArray() }, ".ttf"));
    }

    [Fact]
    public async Task ReconcileAsync_ShouldRecoverCaseOnlyTemporaryHopAfterCrash()
    {
        await using var fixture = await RenameFixture.CreateAsync();
        FontFileRecord source = await fixture.AddAsync(1, "example-regular-v2.003.TTF");
        await fixture.PublishAsync([source]);
        await fixture.SaveAnalysisAsync();
        await fixture.Candidates.GetCurrentAsync(CancellationToken.None);
        OperationRequest request = (await fixture.Normalizer.CreateCurrentRequestAsync(CancellationToken.None))!;
        fixture.FileSystem.AfterMove = () => throw new SimulatedCrashException();
        await Assert.ThrowsAsync<SimulatedCrashException>(() => fixture.Normalizer.ExecuteAsync(request, CancellationToken.None));
        fixture.FileSystem.AfterMove = null;
        await fixture.Normalizer.ReconcileAsync(CancellationToken.None);
        MaintenanceBatch recovered = (await fixture.Repository.GetMaintenanceBatchByOperationRequestAsync(request.Id, CancellationToken.None))!;
        Assert.Equal(RenameItemStatus.Renamed, Assert.Single(recovered.RenameItems).Status);
        Assert.True(System.IO.File.Exists(Path.Combine(fixture.Root, "Example-Regular-v2.003.ttf")));
        Assert.Empty(Directory.GetFiles(fixture.Root, "*.tmp"));
        Assert.Single(fixture.Repository.OperationRequests, operation => operation.Kind == FontOperationKind.Analyze);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldStabilizePartialCancellationAndRetainLaterFiles()
    {
        await using var fixture = await RenameFixture.CreateAsync();
        await fixture.PublishAsync([await fixture.AddAsync(1, "a.ttf"), await fixture.AddAsync(2, "b.ttf")]);
        await fixture.SaveAnalysisAsync();
        await fixture.Candidates.GetCurrentAsync(CancellationToken.None);
        OperationRequest request = (await fixture.Normalizer.CreateCurrentRequestAsync(CancellationToken.None))!;
        using var cancellation = new CancellationTokenSource();
        fixture.FileSystem.AfterMove = cancellation.Cancel;
        var reports = new List<double>();
        MaintenanceBatch batch = await fixture.Normalizer.ExecuteAsync(request, cancellation.Token, new Jellyfin.Plugin.AssSubsetter.Tests.ScheduledTasks.InlineProgress(reports.Add));
        Assert.Contains(47.5d, reports);
        Assert.DoesNotContain(100d, reports);
        Assert.Equal(MaintenanceBatchStatus.Cancelled, batch.Status);
        Assert.Single(batch.RenameItems, item => item.Status == RenameItemStatus.Renamed);
        Assert.Single(batch.RenameItems, item => item.Status == RenameItemStatus.Pending);
        Assert.True(System.IO.File.Exists(Path.Combine(fixture.Root, "b.ttf")));
        Assert.Single(fixture.Repository.OperationRequests, operation => operation.Kind == FontOperationKind.Analyze);
    }

    [Fact]
    public async Task CreatePlanAndExecute_ShouldCarryAppliedCollisionProvenance()
    {
        await using var fixture = await RenameFixture.CreateAsync();
        await fixture.PublishAsync([await fixture.AddAsync(1, "a.ttf"), await fixture.AddAsync(2, "b.ttf")]);
        await fixture.SaveAnalysisAsync();

        FontRenameSnapshot plan = await fixture.Candidates.GetCurrentAsync(CancellationToken.None);
        Assert.All(plan.Items, item => Assert.Contains(item.NamingEvidence.Fields, field => field.Name == "version" && field.Value == "2.003"));
        Assert.Single(plan.Items, item => item.CollisionFingerprint is null);
        Assert.Equal("00112233", Assert.Single(plan.Items, item => item.CollisionFingerprint is not null).CollisionFingerprint);

        OperationRequest request = (await fixture.Normalizer.CreateCurrentRequestAsync(CancellationToken.None))!;
        MaintenanceBatch batch = await fixture.Normalizer.ExecuteAsync(request, CancellationToken.None);
        Assert.Equal(plan.Items.Select(item => (item.SkipReason is null ? item.NamingEvidence.Fields.First(field => field.Name == "version").Value : null, item.CollisionFingerprint)), batch.RenameItems.Select(item => (item.NamingEvidence.Fields.First(field => field.Name == "version").Value, item.CollisionFingerprint)));
    }

    [Fact]
    public async Task RollbackAsync_ShouldNeverOverwriteAnOccupiedOriginalPath()
    {
        await using var fixture = await RenameFixture.CreateAsync();
        FontFileRecord source = await fixture.AddAsync(1, "old.ttf");
        await fixture.PublishAsync([source]);
        await fixture.SaveAnalysisAsync();
        await fixture.Candidates.GetCurrentAsync(CancellationToken.None);
        OperationRequest request = (await fixture.Normalizer.CreateCurrentRequestAsync(CancellationToken.None))!;
        MaintenanceBatch batch = await fixture.Normalizer.ExecuteAsync(request, CancellationToken.None);
        await System.IO.File.WriteAllTextAsync(source.Path, "keep me", TestContext.Current.CancellationToken);
        OperationRequest rollback = await new FontOperationRequestStore(fixture.Repository, TimeProvider.System).CreateAsync(FontOperationKind.RollbackNames, batch.Id, [1], "rollback-collision", CancellationToken.None);
        MaintenanceBatch result = await fixture.Normalizer.RollbackAsync(rollback, CancellationToken.None);
        Assert.Equal(RenameItemStatus.DestinationOccupied, Assert.Single(result.RenameItems).Status);
        Assert.Equal("keep me", await System.IO.File.ReadAllTextAsync(source.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReconcileAsync_ShouldRetryAnalysisRequestPersistenceAfterPathChanged()
    {
        await using var fixture = await RenameFixture.CreateAsync();
        await fixture.PublishAsync([await fixture.AddAsync(1, "old.ttf")]);
        await fixture.SaveAnalysisAsync();
        await fixture.Candidates.GetCurrentAsync(CancellationToken.None);
        OperationRequest request = (await fixture.Normalizer.CreateCurrentRequestAsync(CancellationToken.None))!;
        fixture.FailAnalysisRequestPersistence = true;
        await Assert.ThrowsAsync<SimulatedCrashException>(() => fixture.Normalizer.ExecuteAsync(request, CancellationToken.None));
        Assert.True(System.IO.File.Exists(Path.Combine(fixture.Root, "Example-Regular-v2.003.ttf")));
        RenameManifest interrupted = Assert.Single(await fixture.Manifests.LoadAllAsync(CancellationToken.None));
        Assert.True(interrupted.RefreshRequired);
        Assert.Equal(RenameItemStatus.Renamed, Assert.Single(interrupted.Batch.RenameItems).Status);
        Assert.DoesNotContain(fixture.Repository.OperationRequests, operation => operation.Kind == FontOperationKind.Analyze);
        fixture.FailAnalysisRequestPersistence = false;
        await fixture.Normalizer.ReconcileAsync(CancellationToken.None);
        await fixture.Normalizer.ReconcileAsync(CancellationToken.None);
        OperationRequest analysis = Assert.Single(fixture.Repository.OperationRequests, operation => operation.Kind == FontOperationKind.Analyze);
        Assert.Equal("rename-refresh:" + interrupted.Batch.Id, analysis.IdempotencyKey);
        Assert.False(Assert.Single(await fixture.Manifests.LoadAllAsync(CancellationToken.None)).RefreshRequired);
        Assert.Equal(1, fixture.FileSystem.MoveCount);
    }

    [Fact]
    public async Task CreatePlanAsync_ShouldExcludeHardLinkOutsideTheScanRoots()
    {
        await using var fixture = await RenameFixture.CreateAsync();
        FontFileRecord source = await fixture.AddAsync(1, "old.ttf");
        string alias = fixture.Root + "-external.ttf";
        try
        {
            CreateHardLink(alias, source.Path);
            await fixture.PublishAsync([source]);
            await fixture.SaveAnalysisAsync();
            FontRenameSnapshot plan = await fixture.Candidates.GetCurrentAsync(CancellationToken.None);
            Assert.Equal("hard-link-or-unknown-link-count", Assert.Single(plan.Items).SkipReason);
        }
        finally
        {
            System.IO.File.Delete(alias);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRefuseHardLinkCreatedAfterPlanning()
    {
        await using var fixture = await RenameFixture.CreateAsync();
        FontFileRecord source = await fixture.AddAsync(1, "old.ttf");
        await fixture.PublishAsync([source]);
        await fixture.SaveAnalysisAsync();
        await fixture.Candidates.GetCurrentAsync(CancellationToken.None);
        OperationRequest request = (await fixture.Normalizer.CreateCurrentRequestAsync(CancellationToken.None))!;
        CreateHardLink(Path.Combine(fixture.Root, "new-alias.ttf"), source.Path);
        MaintenanceBatch batch = await fixture.Normalizer.ExecuteAsync(request, CancellationToken.None);
        Assert.Equal(RenameItemStatus.Skipped, Assert.Single(batch.RenameItems).Status);
        Assert.True(System.IO.File.Exists(source.Path));
    }

    private static void CreateHardLink(string path, string existing)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.True(CreateHardLinkWindows(path, existing, IntPtr.Zero));
        }
        else
        {
            Assert.Equal(0, LinkUnix(existing, path));
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(string path, string existing, IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int LinkUnix(string existing, string path);

    [Fact]
    public async Task CreatePlanAsync_ShouldExcludeUnsafeFilesWithoutAuthorizingOrMoving()
    {
        await using var fixture = await RenameFixture.CreateAsync();
        await fixture.PublishAsync([
            await fixture.AddAsync(1, "eligible.ttf"),
            await fixture.AddAsync(2, "protected.ttf") with { DirectoryPolicy = FontDirectoryPolicy.Custom | FontDirectoryPolicy.Protected },
            await fixture.AddAsync(3, "alias.ttf") with { PathAliases = ["alias2.ttf"] },
            await fixture.AddAsync(4, "unknown.ttf") with { Health = FontFileHealth.Unclassifiable },
            await fixture.AddAsync(5, "quarantine.ttf"),
            await fixture.AddAsync(6, "busy.ttf")]);
        AnalysisBatch analysis = await fixture.SaveAnalysisAsync(5);
        RuntimeFontFace runtime = new(6, Path.Combine(fixture.Root, "busy.ttf"), 0, "Example", "Regular", [], 400, 5, 0, false, false, "TrueType", 0, false);
        await using FontFileLease lease = await fixture.Coordinator.AcquireReadAsync(runtime, CancellationToken.None);
        var prepared = await fixture.Candidates.PrepareForPublicationAsync(analysis with { Id = 0 }, CancellationToken.None);
        await fixture.Repository.SaveRenameCandidatesAsync(prepared with { State = prepared.State with { AnalysisBatchId = analysis.Id } }, fixture.Candidates.ReadRequirements(), null, CancellationToken.None);
        FontRenameSnapshot plan = await fixture.Candidates.GetCurrentAsync(CancellationToken.None);
        Assert.Single(plan.Items, item => item.SkipReason is null);
        Assert.Equal(5, plan.Items.Count(item => item.SkipReason is not null));
        Assert.Empty(fixture.Repository.OperationRequests);
        Assert.True(System.IO.File.Exists(Path.Combine(fixture.Root, "eligible.ttf")));
    }

    [Fact]
    public async Task ExecuteAndRollback_ShouldMoveWithoutChangingBytesAndQueueAnalysis()
    {
        await using var fixture = await RenameFixture.CreateAsync();
        FontFileRecord source = await fixture.AddAsync(1, "old.ttf");
        await fixture.PublishAsync([source]);
        await fixture.SaveAnalysisAsync();
        await fixture.Candidates.GetCurrentAsync(CancellationToken.None);
        OperationRequest request = (await fixture.Normalizer.CreateCurrentRequestAsync(CancellationToken.None))!;
        MaintenanceBatch batch = await fixture.Normalizer.ExecuteAsync(request, CancellationToken.None);
        Assert.Equal(RenameItemStatus.Renamed, Assert.Single(batch.RenameItems).Status);
        Assert.False(System.IO.File.Exists(source.Path));
        Assert.Equal(source.Sha256.ToArray(), SHA256.HashData(await System.IO.File.ReadAllBytesAsync(Path.Combine(fixture.Root, "Example-Regular-v2.003.ttf"), TestContext.Current.CancellationToken)));
        Assert.Single(fixture.Repository.OperationRequests, operation => operation.Kind == FontOperationKind.Analyze);
        Assert.All(fixture.Repository.PublishedFiles, file => Assert.Equal(1, file.GenerationId));
        var rollback = await new FontOperationRequestStore(fixture.Repository, TimeProvider.System).CreateAsync(FontOperationKind.RollbackNames, batch.Id, [source.Id], "rollback", CancellationToken.None);
        MaintenanceBatch restored = await fixture.Normalizer.RollbackAsync(rollback, CancellationToken.None);
        Assert.Equal(RenameItemStatus.RolledBack, Assert.Single(restored.RenameItems).Status);
        Assert.True(System.IO.File.Exists(source.Path));
        Assert.Equal(2, fixture.Repository.OperationRequests.Count(operation => operation.Kind == FontOperationKind.Analyze));
        Assert.All(fixture.Repository.PublishedFiles, file => Assert.Equal(1, file.GenerationId));
    }

    [Fact]
    public async Task ExecuteAsync_ShouldFenceOldRuntimePathUntilAnalyzedPublication()
    {
        await using var fixture = await RenameFixture.CreateAsync();
        FontFileRecord source = await fixture.AddAsync(1, "old.ttf");
        await fixture.PublishAsync([source]);
        await fixture.SaveAnalysisAsync();
        await fixture.Candidates.GetCurrentAsync(CancellationToken.None);
        OperationRequest request = (await fixture.Normalizer.CreateCurrentRequestAsync(CancellationToken.None))!;

        await fixture.Normalizer.ExecuteAsync(request, CancellationToken.None);

        Assert.False(fixture.Coordinator.IsPathAvailable(source.Path));
        Assert.True(fixture.Coordinator.IsPathAvailable(Path.Combine(fixture.Root, "Example-Regular-v2.003.ttf")));
    }

    [Fact]
    public async Task Startup_ShouldRestoreRenameFenceBeforeFollowUpAnalysisIsPersisted()
    {
        await using var fixture = await RenameFixture.CreateAsync();
        FontFileRecord source = await fixture.AddAsync(1, "old.ttf");
        await fixture.PublishAsync([source]);
        await fixture.SaveAnalysisAsync();
        await fixture.Candidates.GetCurrentAsync(CancellationToken.None);
        OperationRequest request = (await fixture.Normalizer.CreateCurrentRequestAsync(CancellationToken.None))!;
        fixture.FailAnalysisRequestPersistence = true;
        await Assert.ThrowsAsync<SimulatedCrashException>(() => fixture.Normalizer.ExecuteAsync(request, CancellationToken.None));

        var restartedCoordinator = new FontMaintenanceCoordinator();
        using var manager = Jellyfin.Plugin.AssSubsetter.Tests.Services.FontCacheManagerFixture.CreateManager(fixture.Repository, restartedCoordinator);
        var startup = new FontRuntimeSnapshotStartupService(
            manager,
            NullLogger<FontRuntimeSnapshotStartupService>.Instance,
            fixture.Repository,
            fixture.Manifests,
            new InMemoryQuarantineManifestStore(),
            restartedCoordinator);
        await startup.StartAsync(CancellationToken.None);

        Assert.False(restartedCoordinator.IsPathAvailable(source.Path));
    }

    [Fact]
    public async Task Startup_ShouldNotFenceAPathRestoredByRollback()
    {
        await using var fixture = await RenameFixture.CreateAsync();
        FontFileRecord source = await fixture.AddAsync(1, "old.ttf");
        await fixture.PublishAsync([source]);
        await fixture.SaveAnalysisAsync();
        await fixture.Candidates.GetCurrentAsync(CancellationToken.None);
        OperationRequest rename = (await fixture.Normalizer.CreateCurrentRequestAsync(CancellationToken.None))!;
        fixture.FailAnalysisRequestPersistence = true;
        await Assert.ThrowsAsync<SimulatedCrashException>(() => fixture.Normalizer.ExecuteAsync(rename, CancellationToken.None));
        MaintenanceBatch batch = (await fixture.Repository.GetMaintenanceBatchByOperationRequestAsync(rename.Id, CancellationToken.None))!;
        OperationRequest rollback = await new FontOperationRequestStore(fixture.Repository, TimeProvider.System)
            .CreateAsync(FontOperationKind.RollbackNames, batch.Id, [source.Id], "rollback-before-refresh", CancellationToken.None);
        await Assert.ThrowsAsync<SimulatedCrashException>(() => fixture.Normalizer.RollbackAsync(rollback, CancellationToken.None));

        var restartedCoordinator = new FontMaintenanceCoordinator();
        using var manager = Jellyfin.Plugin.AssSubsetter.Tests.Services.FontCacheManagerFixture.CreateManager(fixture.Repository, restartedCoordinator);
        var startup = new FontRuntimeSnapshotStartupService(
            manager,
            NullLogger<FontRuntimeSnapshotStartupService>.Instance,
            fixture.Repository,
            fixture.Manifests,
            new InMemoryQuarantineManifestStore(),
            restartedCoordinator);
        await startup.StartAsync(CancellationToken.None);

        Assert.True(restartedCoordinator.IsPathAvailable(source.Path));
    }

    [Fact]
    public async Task ExecuteAsync_ShouldRefuseChangedEvidenceAndOccupiedTarget()
    {
        await using var fixture = await RenameFixture.CreateAsync();
        FontFileRecord source = await fixture.AddAsync(1, "old.ttf");
        await fixture.PublishAsync([source]);
        await fixture.SaveAnalysisAsync();
        await fixture.Candidates.GetCurrentAsync(CancellationToken.None);
        OperationRequest request = (await fixture.Normalizer.CreateCurrentRequestAsync(CancellationToken.None))!;
        await System.IO.File.WriteAllTextAsync(Path.Combine(fixture.Root, "Example-Regular-v2.003.ttf"), "occupied", TestContext.Current.CancellationToken);
        MaintenanceBatch batch = await fixture.Normalizer.ExecuteAsync(request, CancellationToken.None);
        Assert.Equal(RenameItemStatus.DestinationOccupied, Assert.Single(batch.RenameItems).Status);
        Assert.True(System.IO.File.Exists(source.Path));
        Assert.Equal("occupied", await System.IO.File.ReadAllTextAsync(Path.Combine(fixture.Root, "Example-Regular-v2.003.ttf"), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("Example Sans", "Regular", "Example Sans-Regular-v2.003.ttf")]
    [InlineData("CON", "Regular", "_CON-Regular-v2.003.ttf")]
    public void BuildSingleFaceName_ShouldBePortable(string family, string style, string expected)
    {
        Assert.Equal(expected, DefaultName(Face(family, style), ".ttf"));
    }

    [Fact]
    public void BuildSingleFaceName_ShouldUseCompleteTypographicOrLegacyPair()
    {
        FontFaceRecord face = Face("Legacy", "Regular") with
        {
            NameRecords = [Name(1, "Legacy"), Name(2, "Regular"), Name(16, "Typographic"), Name(17, "Bold"), Name(6, "PostScript-Bold")]
        };
        Assert.Equal("Typographic-Bold-v2.003.otf", DefaultName(face, ".OTF"));
        Assert.Equal("Typographic-Bold-v2.003.ttf", DefaultName(face with { NameRecords = face.NameRecords.Where(n => n.NameId != 6).ToArray() }, ".ttf"));
        Assert.Null(DefaultName(face with { NameRecords = [] }, ".ttf"));
    }

    [Fact]
    public void BuildSingleFaceName_ShouldPreferWindowsEnglishOverNeutralName()
    {
        FontFaceRecord face = Face("English", "Regular") with
        {
            NameRecords =
            [
                new OpenTypeNameRecord(3, 1, 0, 1, "Neutral"),
                Name(1, "English"),
                Name(2, "Regular")
            ]
        };

        Assert.Equal("English-Regular-v2.003.ttf", DefaultName(face, ".ttf"));
    }

    [Fact]
    public void BuildSingleFaceName_ShouldChooseStableValueForDuplicateLanguageRecords()
    {
        FontFaceRecord face = Face("Alpha", "Regular") with
        {
            NameRecords = [Name(1, "Beta"), Name(1, "Alpha"), Name(2, "Regular")]
        };

        Assert.Equal("Alpha-Regular-v2.003.ttf", DefaultName(face, ".ttf"));
    }

    [Fact]
    public void ResolveCollisions_ShouldPreserveTemplateVersionAndAddOnlyFingerprint()
    {
        FontFileRecord[] files = [File(1, "a.otf", Face("Example", "Regular")), File(2, "b.otf", Face("Example", "Regular") with { StrictFingerprint = Convert.FromHexString("a1b2c3d4".PadRight(64, '0')) })];
        IReadOnlyDictionary<long, string?> names = Resolve(files);
        Assert.Equal("Example-Regular-v2.003.otf", names[1]);
        Assert.Equal("Example-Regular-v2.003-a1b2c3d4.otf", names[2]);
    }

    [Fact]
    public void CollectionName_ShouldDescribeFamiliesAndOrderedFaceSet()
    {
        FontFaceRecord first = Face("Example", "Regular");
        FontFaceRecord second = Face("Example", "Bold") with { FaceIndex = 1 };
        Assert.Equal("Example-Collection-v2.003.ttc", DefaultName(File(1, "old.ttc", first, second)));
        FontFileRecord mixed = File(2, "old.otc", first, second with { NameRecords = [Name(1, "Other"), Name(2, "Bold")] });
        string name = DefaultName(mixed)!;
        Assert.Equal("Example+Other-MixedCollection-v2.003.otc", name);
        Assert.Equal(name, DefaultName(mixed with { Faces = mixed.Faces.Reverse().ToArray() }));
    }

    [Fact]
    public async Task MoveCreatesTargetAndRollbackRecreatesOldParents()
    {
        await using var fixture = await RenameFixture.CreateAsync();
        fixture.Rules = new("{family}-{style}", "organized/{family}", true);
        FontFileRecord source = await fixture.AddAsync(1, Path.Combine("old", "a", "font.ttf"));
        await fixture.PublishAsync([source]);
        await fixture.SaveAnalysisAsync();
        FontRenameSnapshot plan = await fixture.Candidates.GetCurrentAsync(CancellationToken.None);
        string target = Path.Combine(fixture.Root, "organized", "Example", "Example-Regular.ttf");
        Assert.Equal(target, plan.Items.Single().NewPath);
        OperationRequest request = (await fixture.Normalizer.CreateCurrentRequestAsync(CancellationToken.None))!;
        MaintenanceBatch batch = await fixture.Normalizer.ExecuteAsync(request, CancellationToken.None);
        Assert.Equal(RenameItemStatus.Renamed, batch.RenameItems.Single().Status);
        Assert.Equal(source.Sha256.ToArray(), SHA256.HashData(await System.IO.File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken)));
        Assert.False(Directory.Exists(Path.GetDirectoryName(source.Path)));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "old")));
        Assert.True(Directory.Exists(fixture.Root));
        fixture.Rules = new("{name}", "changed", false);
        OperationRequest rollback = await new FontOperationRequestStore(fixture.Repository, TimeProvider.System).CreateAsync(FontOperationKind.RollbackNames, batch.Id, [1], "directory-rollback", CancellationToken.None);
        MaintenanceBatch restored = await fixture.Normalizer.RollbackAsync(rollback, CancellationToken.None);
        Assert.Equal(RenameItemStatus.RolledBack, restored.RenameItems.Single().Status);
        Assert.True(System.IO.File.Exists(source.Path));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "organized")));
    }

    [Fact]
    public async Task DirectoryWarningDoesNotUndoRenamedStatus()
    {
        await using var fixture = await RenameFixture.CreateAsync();
        fixture.Rules = new("{family}-{style}", "organized", true);
        fixture.Directories.RemoveError = "directory-remove-failed: denied";
        var source = await fixture.AddAsync(1, Path.Combine("old", "font.ttf"));
        await fixture.PublishAsync([source]);
        await fixture.SaveAnalysisAsync();
        await fixture.Candidates.GetCurrentAsync(CancellationToken.None);
        OperationRequest request = (await fixture.Normalizer.CreateCurrentRequestAsync(CancellationToken.None))!;
        MaintenanceBatch batch = await fixture.Normalizer.ExecuteAsync(request, CancellationToken.None);
        Assert.Equal(RenameItemStatus.Renamed, batch.RenameItems.Single().Status);
        var manifest = (await fixture.Manifests.LoadAllAsync(CancellationToken.None)).Single();
        Assert.Contains(manifest.Directories, item => item.Status == RenameDirectoryStatus.Warning);
        Assert.True(Directory.Exists(Path.GetDirectoryName(source.Path)));
        Assert.Contains("Directories", batch.SummaryJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CrashAfterDirectoryIntentDoesNotClaimExistingDirectory()
    {
        await using var fixture = await RenameFixture.CreateAsync();
        fixture.Rules = new("{family}-{style}", "organized", false);
        var source = await fixture.AddAsync(1, "old.ttf");
        await fixture.PublishAsync([source]);
        await fixture.SaveAnalysisAsync();
        await fixture.Candidates.GetCurrentAsync(CancellationToken.None);
        OperationRequest request = (await fixture.Normalizer.CreateCurrentRequestAsync(CancellationToken.None))!;
        fixture.Directories.BeforeCreate = () => throw new SimulatedCrashException();
        await Assert.ThrowsAsync<SimulatedCrashException>(() => fixture.Normalizer.ExecuteAsync(request, CancellationToken.None));
        var intent = (await fixture.Manifests.LoadAllAsync(CancellationToken.None)).Single();
        Assert.Equal(RenameDirectoryStatus.Planned, intent.Directories.Single().Status);
        Directory.CreateDirectory(Path.Combine(fixture.Root, "organized"));
        fixture.Directories.BeforeCreate = null;
        await fixture.Normalizer.ReconcileAsync(CancellationToken.None);
        await fixture.Normalizer.ReconcileAsync(CancellationToken.None);
        var manifest = (await fixture.Manifests.LoadAllAsync(CancellationToken.None)).Single();
        Assert.Null(manifest.Directories.Single(item => item.Action == RenameDirectoryAction.Create).CreatedByBatch);
        Assert.Equal(RenameItemStatus.Renamed, manifest.Batch.RenameItems.Single().Status);
        Assert.Equal(1, fixture.FileSystem.MoveCount);
    }

    [Fact]
    public async Task CrashAfterMoveResumesWithoutSecondRename()
    {
        await using var fixture = await RenameFixture.CreateAsync();
        fixture.Rules = new("{family}-{style}", "organized", true);
        var source = await fixture.AddAsync(1, Path.Combine("old", "a", "font.ttf"));
        await fixture.PublishAsync([source]);
        await fixture.SaveAnalysisAsync();
        await fixture.Candidates.GetCurrentAsync(CancellationToken.None);
        OperationRequest request = (await fixture.Normalizer.CreateCurrentRequestAsync(CancellationToken.None))!;
        fixture.FileSystem.AfterMove = () => throw new SimulatedCrashException();
        await Assert.ThrowsAsync<SimulatedCrashException>(() => fixture.Normalizer.ExecuteAsync(request, CancellationToken.None));
        fixture.FileSystem.AfterMove = null;
        await fixture.Normalizer.ReconcileAsync(CancellationToken.None);
        await fixture.Normalizer.ReconcileAsync(CancellationToken.None);
        Assert.Equal(1, fixture.FileSystem.MoveCount);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "old")));
        var batch = (await fixture.Repository.GetMaintenanceBatchByOperationRequestAsync(request.Id, CancellationToken.None))!;
        Assert.Equal(RenameItemStatus.Renamed, batch.RenameItems.Single().Status);
        Assert.Contains("RemoveEmpty", batch.SummaryJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancellationKeepsCompletedItemsAndCleansUnusedCreatedDirectories()
    {
        await using var fixture = await RenameFixture.CreateAsync();
        fixture.Rules = new("{family}-{style}", "organized", false);
        var source = await fixture.AddAsync(1, "old.ttf");
        await fixture.PublishAsync([source]);
        await fixture.SaveAnalysisAsync();
        await fixture.Candidates.GetCurrentAsync(CancellationToken.None);
        OperationRequest request = (await fixture.Normalizer.CreateCurrentRequestAsync(CancellationToken.None))!;
        using var cancellation = new CancellationTokenSource();
        fixture.Directories.AfterCreate = cancellation.Cancel;
        MaintenanceBatch result = await fixture.Normalizer.ExecuteAsync(request, cancellation.Token);
        Assert.Equal(MaintenanceBatchStatus.Cancelled, result.Status);
        Assert.True(System.IO.File.Exists(source.Path));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "organized")));
        Assert.Equal(0, fixture.FileSystem.MoveCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HiddenOrConcurrentFilesStopAncestorCleanup(bool concurrent)
    {
        await using var fixture = await RenameFixture.CreateAsync();
        fixture.Rules = new("{family}-{style}", "organized", true);
        var source = await fixture.AddAsync(1, Path.Combine("old", "a", "font.ttf"));
        string hidden = Path.Combine(Path.GetDirectoryName(source.Path)!, ".hidden");
        if (concurrent) fixture.Directories.BeforeRemove = path => System.IO.File.WriteAllText(Path.Combine(path, ".hidden"), "keep");
        else await System.IO.File.WriteAllTextAsync(hidden, "keep", TestContext.Current.CancellationToken);
        await fixture.PublishAsync([source]);
        await fixture.SaveAnalysisAsync();
        await fixture.Candidates.GetCurrentAsync(CancellationToken.None);
        OperationRequest request = (await fixture.Normalizer.CreateCurrentRequestAsync(CancellationToken.None))!;
        MaintenanceBatch result = await fixture.Normalizer.ExecuteAsync(request, CancellationToken.None);
        Assert.Equal(RenameItemStatus.Renamed, result.RenameItems.Single().Status);
        Assert.Equal("keep", await System.IO.File.ReadAllTextAsync(hidden, TestContext.Current.CancellationToken));
        Assert.True(Directory.Exists(Path.Combine(fixture.Root, "old")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupStopsAtOtherConfiguredRootOrActiveLease(bool activeLease)
    {
        await using var fixture = await RenameFixture.CreateAsync();
        fixture.Rules = new("{family}-{style}", "organized", true);
        var source = await fixture.AddAsync(1, Path.Combine("old", "a", "font.ttf"));
        await fixture.PublishAsync([source]);
        await fixture.SaveAnalysisAsync();
        await fixture.Candidates.GetCurrentAsync(CancellationToken.None);
        OperationRequest request = (await fixture.Normalizer.CreateCurrentRequestAsync(CancellationToken.None))!;
        IAsyncDisposable? lease = null;
        fixture.FileSystem.AfterMove = () =>
        {
            if (activeLease) lease = fixture.Coordinator.AcquireExclusiveAsync([Path.Combine(Path.GetDirectoryName(source.Path)!, "reserved.ttf")], CancellationToken.None).GetAwaiter().GetResult();
            else fixture.AdditionalRoots = ";" + Path.GetDirectoryName(source.Path);
        };
        try
        {
            MaintenanceBatch result = await fixture.Normalizer.ExecuteAsync(request, CancellationToken.None);
            Assert.Equal(RenameItemStatus.Renamed, result.RenameItems.Single().Status);
            Assert.True(Directory.Exists(Path.GetDirectoryName(source.Path)));
            Assert.Contains((await fixture.Manifests.LoadAllAsync(CancellationToken.None)).Single().Directories, item => item.Status == RenameDirectoryStatus.Retained);
        }
        finally
        {
            if (lease is not null) await lease.DisposeAsync();
        }
    }

    [Fact]
    public async Task NewRuleRejectsUnstartedBatchButResumesSavedManifest()
    {
        await using var fixture = await RenameFixture.CreateAsync();
        fixture.Rules = new("{family}-{style}", "organized", true);
        var source = await fixture.AddAsync(1, Path.Combine("old", "font.ttf"));
        await fixture.PublishAsync([source]);
        await fixture.SaveAnalysisAsync();
        await fixture.Candidates.GetCurrentAsync(CancellationToken.None);
        OperationRequest request = (await fixture.Normalizer.CreateCurrentRequestAsync(CancellationToken.None))!;
        FontNamingRules originalRules = fixture.Rules;
        fixture.Rules = new("{name}", "changed", false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Normalizer.ExecuteAsync(request, CancellationToken.None));
        Assert.Empty(await fixture.Manifests.LoadAllAsync(CancellationToken.None));
        fixture.Rules = originalRules;
        fixture.FileSystem.AfterMove = () => throw new SimulatedCrashException();
        await Assert.ThrowsAsync<SimulatedCrashException>(() => fixture.Normalizer.ExecuteAsync(request, CancellationToken.None));
        fixture.FileSystem.AfterMove = null;
        fixture.Rules = new("{name}", "changed", false);
        MaintenanceBatch result = await fixture.Normalizer.ExecuteAsync(request, CancellationToken.None);
        Assert.Equal(RenameItemStatus.Renamed, result.RenameItems.Single().Status);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "changed")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "old")));
        Assert.Equal(originalRules, (await fixture.Manifests.LoadAllAsync(CancellationToken.None)).Single().Rules);
    }

    [Fact]
    public async Task FailedSourceEvidenceResolvesUnusedDirectoryIntentWithoutClaimingOwnership()
    {
        await using var fixture = await RenameFixture.CreateAsync();
        fixture.Rules = new("{family}-{style}", "organized", false);
        var source = await fixture.AddAsync(1, "old.ttf");
        await fixture.PublishAsync([source]);
        await fixture.SaveAnalysisAsync();
        await fixture.Candidates.GetCurrentAsync(CancellationToken.None);
        OperationRequest request = (await fixture.Normalizer.CreateCurrentRequestAsync(CancellationToken.None))!;
        fixture.Directories.BeforeCreate = () => throw new SimulatedCrashException();
        await Assert.ThrowsAsync<SimulatedCrashException>(() => fixture.Normalizer.ExecuteAsync(request, CancellationToken.None));
        fixture.Directories.BeforeCreate = null;
        string target = Path.Combine(fixture.Root, "organized");
        Directory.CreateDirectory(target);
        await System.IO.File.WriteAllTextAsync(source.Path, "changed", TestContext.Current.CancellationToken);
        await fixture.Normalizer.ReconcileAsync(CancellationToken.None);
        await fixture.Normalizer.ReconcileAsync(CancellationToken.None);
        var manifest = (await fixture.Manifests.LoadAllAsync(CancellationToken.None)).Single();
        Assert.Equal(RenameItemStatus.EvidenceDrift, manifest.Batch.RenameItems.Single().Status);
        Assert.DoesNotContain(manifest.Directories, item => item.Status == RenameDirectoryStatus.Planned);
        Assert.Null(manifest.Directories.Single().CreatedByBatch);
        Assert.True(Directory.Exists(target));
        Assert.Equal("changed", await System.IO.File.ReadAllTextAsync(source.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CrashAfterCleanupIntentReplaysRemovalWithoutMovingAgain()
    {
        await using var fixture = await RenameFixture.CreateAsync();
        fixture.Rules = new("{family}-{style}", "organized", true);
        var source = await fixture.AddAsync(1, Path.Combine("old", "a", "font.ttf"));
        await fixture.PublishAsync([source]);
        await fixture.SaveAnalysisAsync();
        await fixture.Candidates.GetCurrentAsync(CancellationToken.None);
        OperationRequest request = (await fixture.Normalizer.CreateCurrentRequestAsync(CancellationToken.None))!;
        fixture.Directories.BeforeRemove = _ => throw new SimulatedCrashException();
        await Assert.ThrowsAsync<SimulatedCrashException>(() => fixture.Normalizer.ExecuteAsync(request, CancellationToken.None));
        fixture.Directories.BeforeRemove = null;
        await fixture.Normalizer.ReconcileAsync(CancellationToken.None);
        await fixture.Normalizer.ReconcileAsync(CancellationToken.None);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "old")));
        Assert.Equal(1, fixture.FileSystem.MoveCount);
        Assert.Equal(RenameItemStatus.Renamed, (await fixture.Manifests.LoadAllAsync(CancellationToken.None)).Single().Batch.RenameItems.Single().Status);
    }

    [Fact]
    public async Task RuleChangeAfterQueueRejectsSavedAuthorizationWithoutCreatingIntent()
    {
        await using var fixture = await RenameFixture.CreateAsync();
        var source = await fixture.AddAsync(1, "old.ttf");
        await fixture.PublishAsync([source]);
        var analysis = await fixture.SaveAnalysisAsync();
        var request = (await fixture.Normalizer.CreateCurrentRequestAsync(CancellationToken.None))!;
        string saved = request.RequestJson;
        fixture.Rules = fixture.Rules with { FolderTemplate = "changed" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Normalizer.ExecuteAsync(request, CancellationToken.None));
        Assert.True(System.IO.File.Exists(source.Path));
        Assert.Equal(saved, (await fixture.Repository.GetOperationRequestAsync(request.Id, CancellationToken.None))!.RequestJson);
        Assert.Equal(AnalysisBatchStatus.Complete, (await fixture.Repository.GetAnalysisBatchAsync(analysis.Id, CancellationToken.None))!.Status);
        Assert.Empty(await fixture.Manifests.LoadAllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task MoveIntentWithoutPhysicalChangeDoesNotInvalidateAnalysisOrQueueRefresh()
    {
        await using var fixture = await RenameFixture.CreateAsync();
        await fixture.PublishAsync([await fixture.AddAsync(1, "old.ttf")]);
        var analysis = await fixture.SaveAnalysisAsync();
        var request = (await fixture.Normalizer.CreateCurrentRequestAsync(CancellationToken.None))!;
        fixture.FileSystem.BeforeMove = () => throw new IOException("move refused");
        await fixture.Normalizer.ExecuteAsync(request, CancellationToken.None);
        Assert.Equal(AnalysisBatchStatus.Complete, (await fixture.Repository.GetAnalysisBatchAsync(analysis.Id, CancellationToken.None))!.Status);
        Assert.DoesNotContain(fixture.Repository.OperationRequests, item => item.Kind == FontOperationKind.Analyze);
        Assert.False((await fixture.Manifests.LoadAllAsync(CancellationToken.None)).Single().RefreshRequired);
    }

    private static string LongParent(int length)
        => Path.Combine(Path.TrimEndingDirectorySeparator(Path.GetTempPath()), new string('p', length - Path.TrimEndingDirectorySeparator(Path.GetTempPath()).Length - 1));

    private static string? DefaultName(FontFaceRecord face, string extension)
        => DefaultName(File(1, "unused" + extension, face));

    private static string? DefaultName(FontFileRecord file)
    {
        var program = Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance.Naming.FontNamingTemplate.Compile(FontNamingRules.DefaultFileNameTemplate, FontTemplateKind.FileName).Program!;
        var result = Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance.Naming.FontNamingTemplate.Expand(program, FontNamingFieldResolver.Resolve(file));
        return result.SkipReason is null ? result.Components.Single() + Path.GetExtension(file.Path).ToLowerInvariant() : null;
    }

    private static IReadOnlyDictionary<long, string?> Resolve(IReadOnlyList<FontFileRecord> files) => ResolveState(files).Names;

    private static (IReadOnlyDictionary<long, string?> Names, IReadOnlyDictionary<long, (string? Version, string? Fingerprint)> Provenance) ResolveState(IReadOnlyList<FontFileRecord> files)
    {
        var plan = new FontRenamePlanner(new PlanningDirectories()).Build(1, 1, files, new HashSet<long>(), new(FontNamingRules.DefaultFileNameTemplate, "", false), new FontDirectoryPolicyResolver(Path.TrimEndingDirectorySeparator(Path.GetTempPath()), null, null, false));
        return (plan.Items.ToDictionary(item => item.File.Id, item => item.SkipReason is null && item.NewPath is not null ? Path.GetFileName(item.NewPath) : null),
            plan.Items.ToDictionary(item => item.File.Id, item => (item.SkipReason is null ? item.NamingEvidence.Fields.First(field => field.Name == "version").Value : null, item.CollisionFingerprint)));
    }

    private sealed class PlanningDirectories : IFontRenameDirectoryOperations
    {
        public FontRenameDirectoryProbe Probe(string file, string root, string target) => new(true, true, null);
        public Task<FontRenameDirectoryResult> CreateDirectoryAsync(string path, string root, FontDirectoryPolicyResolver policy, CancellationToken token) => throw new NotSupportedException();
        public Task<FontRenameDirectoryResult> TryRemoveEmptyAsync(string path, string root, FontDirectoryPolicyResolver policy, CancellationToken token) => throw new NotSupportedException();
    }

    private static OpenTypeNameRecord Name(ushort id, string value) => new(3, 1, 1033, id, value);

    private static FontFaceRecord Face(string family, string style) => new()
    {
        FaceIndex = 0,
        FamilyName = family,
        StyleName = style,
        Names = [family],
        NameRecords = [Name(1, family), Name(2, style)],
        StrictFingerprint = Convert.FromHexString("00112233".PadRight(64, '0')),
        Technology = "TrueType",
        Version = "Version 2.003"
    };

    private static FontFileRecord File(long id, string name, params FontFaceRecord[] faces) => new()
    {
        Id = id,
        Path = Path.Combine(Path.GetTempPath(), name),
        NormalizedPath = name,
        PhysicalIdentity = name,
        Sha256 = new byte[32],
        Faces = faces,
        Health = FontFileHealth.Healthy,
        DirectoryPolicy = FontDirectoryPolicy.Custom
    };

    private sealed class RenameFixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "rename-" + Guid.NewGuid().ToString("N"));
        public InMemoryFontRepository Repository { get; } = new();
        public FontMaintenanceCoordinator Coordinator { get; } = new();
        public FontRenameCandidateService Candidates { get; private set; } = null!;
        public FontFilenameNormalizer Normalizer { get; private set; } = null!;
        public FileRenameManifestStore Manifests { get; private set; } = null!;
        public bool FailAnalysisRequestPersistence { get; set; }
        public ObservedFileSystem FileSystem { get; } = new();
        public ObservedDirectories Directories { get; } = new();
        public string AdditionalRoots { get; set; } = "";
        public FontNamingRules Rules { get; set; } = new(FontNamingRules.DefaultFileNameTemplate, "", false);

        public static Task<RenameFixture> CreateAsync()
        {
            var fixture = new RenameFixture();
            Directory.CreateDirectory(fixture.Root);
            fixture.Manifests = new FileRenameManifestStore(Path.Combine(fixture.Root, "manifests"));
            fixture.Repository.BeforeOperationRequestCreate = request =>
            {
                if (fixture.FailAnalysisRequestPersistence && request.Kind == FontOperationKind.Analyze) throw new SimulatedCrashException();
            };
            fixture.Candidates = new FontRenameCandidateService(fixture.Repository, new FontRenamePlanner(fixture.Directories, fixture.Coordinator.IsInUse), () => fixture.Rules, () => new FontDirectoryPolicyResolver(fixture.Root + fixture.AdditionalRoots, null, null, false), TimeProvider.System, fixture.FileSystem, fixture.Directories, fixture.Coordinator);
            fixture.Normalizer = new FontFilenameNormalizer(fixture.Repository, fixture.Coordinator, fixture.FileSystem, fixture.Manifests, () => new FontDirectoryPolicyResolver(fixture.Root + fixture.AdditionalRoots, null, null, false), new PlatformPhysicalFileIdentityProvider(), fixture.Candidates, fixture.Directories);
            return Task.FromResult(fixture);
        }

        public async Task<FontFileRecord> AddAsync(long id, string name)
        {
            string path = Path.Combine(Root, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            byte[] bytes = [(byte)id, 1, 2, 3];
            await System.IO.File.WriteAllBytesAsync(path, bytes);
            return File(id, name, Face("Example", "Regular")) with { Path = path, PhysicalIdentity = new PlatformPhysicalFileIdentityProvider().GetIdentity(path), Size = bytes.Length, ModifiedUtc = System.IO.File.GetLastWriteTimeUtc(path), Sha256 = SHA256.HashData(bytes) };
        }

        public async Task PublishAsync(IReadOnlyList<FontFileRecord> files)
        {
            long generation = await Repository.BeginGenerationAsync(FontInventoryInputComparer.Capture([], new FontDirectoryPolicyResolver(Root + AdditionalRoots, null, null, false), Jellyfin.Plugin.AssSubsetter.Fonts.Analysis.PhysicalFontAnalysisEvidenceSource.AnalysisCompatibilityKey), [], CancellationToken.None);
            await Repository.WriteInventoryAsync(generation, files.Select(file => file with { GenerationId = generation }).ToArray(), CancellationToken.None);

        }

        public async Task<AnalysisBatch> SaveAnalysisAsync(long? quarantineId = null)
        {
            var batch = await Repository.PublishAnalysisAsync(new AnalysisBatch
            {
                GenerationId = 1,
                Status = AnalysisBatchStatus.Complete,
                EvidenceVersion = Jellyfin.Plugin.AssSubsetter.Fonts.Analysis.FontDuplicateAnalyzer.CurrentEvidenceVersion,
                SummaryJson = "{}",
                Groups = quarantineId is null ? [] : [new DuplicateGroupRecord { EvidenceJson = "{}", Members = [new DuplicateGroupMemberRecord { FileId = quarantineId.Value, Decision = DuplicateGroupMemberDecision.QuarantineCandidate, ScoreJson = "{}", Reason = "duplicate" }] }]
            }, CancellationToken.None);
            var prepared = await Candidates.PrepareForPublicationAsync(batch with { Id = 0 }, CancellationToken.None);
            await Repository.SaveRenameCandidatesAsync(prepared with { State = prepared.State with { AnalysisBatchId = batch.Id } }, Candidates.ReadRequirements(), null, CancellationToken.None);
            return batch;
        }

        public ValueTask DisposeAsync()
        {
            Directory.Delete(Root, true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ObservedDirectories : IFontRenameDirectoryOperations
    {
        private readonly PhysicalFontRenameDirectoryOperations _physical = new();
        public string? RemoveError { get; set; }
        public Action? BeforeCreate { get; set; }
        public Action? AfterCreate { get; set; }
        public Action<string>? BeforeRemove { get; set; }
        public FontRenameDirectoryProbe Probe(string file, string root, string target) => _physical.Probe(file, root, target);
        public async Task<FontRenameDirectoryResult> CreateDirectoryAsync(string path, string root, FontDirectoryPolicyResolver policy, CancellationToken token)
        {
            BeforeCreate?.Invoke();
            var result = await _physical.CreateDirectoryAsync(path, root, policy, token);
            AfterCreate?.Invoke();
            return result;
        }
        public Task<FontRenameDirectoryResult> TryRemoveEmptyAsync(string path, string root, FontDirectoryPolicyResolver policy, CancellationToken token)
        {
            BeforeRemove?.Invoke(path);
            return RemoveError is null ? _physical.TryRemoveEmptyAsync(path, root, policy, token) : Task.FromResult(new FontRenameDirectoryResult(false, null, RemoveError));
        }
    }

    private sealed class SimulatedCrashException : Exception;

    private sealed class ObservedFileSystem : IQuarantineFileSystem
    {
        private readonly PhysicalQuarantineFileSystem _physical = new();
        public Action? BeforeMove { get; set; }
        public Action? AfterMove { get; set; }
        public int MoveCount { get; private set; }
        public bool FileExists(string path) => _physical.FileExists(path);
        public bool IsSameVolume(string sourcePath, string destinationPath) => _physical.IsSameVolume(sourcePath, destinationPath);
        public bool IsPhysicalPath(string path) => _physical.IsPhysicalPath(path);
        public Task<long> GetAvailableSpaceAsync(string path, CancellationToken token) => _physical.GetAvailableSpaceAsync(path, token);
        public Task CreateDirectoryAsync(string path, CancellationToken token) => _physical.CreateDirectoryAsync(path, token);
        public Task<QuarantineFileEvidence?> ReadEvidenceAsync(string path, CancellationToken token) => _physical.ReadEvidenceAsync(path, token);
        public Task CopyAsync(string source, string destination, CancellationToken token) => _physical.CopyAsync(source, destination, token);
        public Task DeleteAsync(string path, CancellationToken token) => _physical.DeleteAsync(path, token);
        public async Task MoveAsync(string source, string destination, CancellationToken token)
        {
            BeforeMove?.Invoke();
            await _physical.MoveAsync(source, destination, token);
            MoveCount++;
            AfterMove?.Invoke();
        }
    }
}
