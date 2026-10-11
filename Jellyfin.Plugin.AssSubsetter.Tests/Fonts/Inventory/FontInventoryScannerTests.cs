using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Persistence;
using SkiaSharp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Inventory;

public sealed class FontInventoryScannerTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "FontScannerTests_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ValidatePublicationInputsAsync_ShouldRejectContentOrPolicyDrift()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Join(_root, "drift.ttf");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4], TestContext.Current.CancellationToken);
        var repository = new InMemoryFontRepository();
        string? protectedRoot = null;
        using var scanner = new FontInventoryScanner(repository,
            () => new FontDirectoryPolicyResolver(_root, protectedRoot, null, false),
            new PathPhysicalFileIdentityProvider(), NullLogger<FontInventoryScanner>.Instance);
        FontInventoryScanResult scan = await scanner.StageAsync(cancellationToken: TestContext.Current.CancellationToken);
        protectedRoot = _root;
        await Assert.ThrowsAsync<InvalidOperationException>(() => scanner.ValidatePublicationInputsAsync(scan, CancellationToken.None));
        protectedRoot = null;
        DateTime modified = File.GetLastWriteTimeUtc(path);
        await File.WriteAllBytesAsync(path, [4, 3, 2, 1], TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(path, modified);
        await Assert.ThrowsAsync<InvalidOperationException>(() => scanner.ValidatePublicationInputsAsync(scan, CancellationToken.None));
        Assert.Empty(repository.PublishedFiles);
    }

    [Fact]
    public async Task InitialEmptyInventory_ShouldStageThenReuseCompletePublication()
    {
        Directory.CreateDirectory(_root);
        var repository = new InMemoryFontRepository();
        using FontInventoryScanner scanner = CreateScanner(repository);
        FontInventoryScanResult first = await scanner.StageAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(first.HasChanges);
        Assert.Contains("initial-scan", first.ChangeReasons);
        await repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = first.GenerationId, Status = AnalysisBatchStatus.Complete, SummaryJson = "{}" }, CancellationToken.None);

        FontInventoryScanResult second = await scanner.StageAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(second.HasChanges);
        Assert.Equal(first.GenerationId, second.GenerationId);
        FontInventoryScanResult repair = await scanner.StageAsync(forceRebuild: true, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(repair.HasChanges);
        Assert.Contains("derived-data-rebuild", repair.ChangeReasons);
    }

    [Fact]
    public void CanonicalInputs_ShouldIgnoreOrderingAndDetectPolicyOrEvidenceChanges()
    {
        string second = Path.Join(_root, "second");
        var initial = new FontDirectoryPolicyResolver(_root + ";" + second, null, null, false);
        var reordered = new FontDirectoryPolicyResolver(second + ";" + _root, null, null, false);
        FontAnalysisInputSnapshot before = FontInventoryInputComparer.Capture([], initial, "v1");
        Assert.Equal(before, FontInventoryInputComparer.Capture([], reordered, "v1"));
        Assert.Contains("directory-policy-changed", FontInventoryInputComparer.GetChangeReasons(before, FontInventoryInputComparer.Capture([], new FontDirectoryPolicyResolver(_root + ";" + second, _root, null, false), "v1")));
        Assert.Contains("directory-policy-changed", FontInventoryInputComparer.GetChangeReasons(before, FontInventoryInputComparer.Capture([], new FontDirectoryPolicyResolver(_root + ";" + second, null, _root, false), "v1")));
        Assert.Contains("evidence-version-changed", FontInventoryInputComparer.GetChangeReasons(before, FontInventoryInputComparer.Capture([], initial, "v2")));
    }

    [Fact]
    public async Task UnchangedScan_ShouldReusePublishedGenerationAndFileIds()
    {
        Directory.CreateDirectory(_root);
        using SKStreamAsset stream = SKTypeface.Default.OpenStream();
        using SKData data = SKData.Create(stream);
        await File.WriteAllBytesAsync(Path.Join(_root, "stable.ttf"), data.ToArray(), TestContext.Current.CancellationToken);
        var repository = new InMemoryFontRepository();
        using FontInventoryScanner scanner = CreateScanner(repository);
        await StageAndPublishAsync(scanner, repository);
        FontFileRecord before = Assert.Single(repository.PublishedFiles);
        FontInventoryScanResult repeated = await scanner.StageAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(repeated.HasChanges);
        FontFileRecord after = Assert.Single(repository.PublishedFiles);
        Assert.Equal(before.GenerationId, after.GenerationId);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.Faces[0].Id, after.Faces[0].Id);
    }

    [Fact]
    public async Task SameLengthAndMtimeContentChange_ShouldRequireNewGeneration()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Join(_root, "changed.ttf");
        using SKStreamAsset stream = SKTypeface.Default.OpenStream();
        using SKData data = SKData.Create(stream);
        byte[] bytes = data.ToArray();
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        var repository = new InMemoryFontRepository();
        using FontInventoryScanner scanner = CreateScanner(repository);
        await StageAndPublishAsync(scanner, repository);
        FontFileRecord before = Assert.Single(repository.PublishedFiles);
        DateTime modified = File.GetLastWriteTimeUtc(path);
        bytes[^1] ^= 1;
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(path, modified);
        await StageAndPublishAsync(scanner, repository);
        FontFileRecord after = Assert.Single(repository.PublishedFiles);
        Assert.NotEqual(before.GenerationId, after.GenerationId);
        Assert.False(before.Sha256.Span.SequenceEqual(after.Sha256.Span));
    }

    [Fact]
    public async Task UnavailablePreviouslyScannedRoot_ShouldFailWithoutReplacingPublication()
    {
        Directory.CreateDirectory(_root);
        var repository = new InMemoryFontRepository();
        using FontInventoryScanner scanner = CreateScanner(repository);
        await StageAndPublishAsync(scanner, repository);
        long before = (await repository.GetLatestCompleteAnalysisAsync(CancellationToken.None))!.GenerationId;
        Directory.Delete(_root);
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => StageAndPublishAsync(scanner, repository));
        Assert.Equal(before, (await repository.GetLatestCompleteAnalysisAsync(CancellationToken.None))!.GenerationId);
    }
    [Fact]
    public async Task RefreshAsync_ShouldRetainTypedNameEvidenceDuringUnchangedReuse()
    {
        Directory.CreateDirectory(_root);
        using SKStreamAsset stream = SKTypeface.Default.OpenStream();
        using SKData data = SKData.Create(stream);
        await File.WriteAllBytesAsync(Path.Combine(_root, "named.ttf"), data.ToArray(), TestContext.Current.CancellationToken);
        var repository = new InMemoryFontRepository();
        using FontInventoryScanner scanner = CreateScanner(repository);
        await StageAndPublishAsync(scanner, repository);
        FontFileRecord legacy = Assert.Single(repository.PublishedFiles);
        long incompleteGeneration = await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await repository.WriteInventoryAsync(incompleteGeneration, [legacy with { GenerationId = incompleteGeneration, Faces = legacy.Faces.Select(face => face with { NameRecords = [] }).ToArray() }], CancellationToken.None);
        await repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = incompleteGeneration, Status = AnalysisBatchStatus.Complete, SummaryJson = "{\"faceScores\":[]}" }, CancellationToken.None);
        await StageAndPublishAsync(scanner, repository);
        Assert.Equal(0, repository.PerformanceSamples[^1].ReusedCount);
        await StageAndPublishAsync(scanner, repository);
        FontFileRecord file = Assert.Single(repository.PublishedFiles);
        Assert.True(file.Health == FontFileHealth.Healthy, file.Error);
        FontFaceRecord face = Assert.Single(file.Faces);
        Assert.Contains(face.NameRecords, name => name.NameId == 6 && !string.IsNullOrWhiteSpace(name.Value));
        Assert.Equal(1, repository.PerformanceSamples[^1].ReusedCount);
        Assert.True(File.Exists(Path.Combine(_root, "named.ttf")));
    }

    [Fact]
    public async Task RefreshAsync_ShouldReinspectIncompleteCoverageBeforeReuse()
    {
        Directory.CreateDirectory(_root);
        using SKStreamAsset stream = SKTypeface.Default.OpenStream();
        using SKData data = SKData.Create(stream);
        await File.WriteAllBytesAsync(Path.Combine(_root, "coverage.ttf"), data.ToArray(), TestContext.Current.CancellationToken);
        var repository = new InMemoryFontRepository();
        using FontInventoryScanner scanner = CreateScanner(repository);
        await StageAndPublishAsync(scanner, repository);
        FontFileRecord initial = Assert.Single(repository.PublishedFiles);
        Assert.NotEmpty(Assert.Single(initial.Faces).UnicodeCoverage);

        long incompleteGeneration = await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await repository.WriteInventoryAsync(
            incompleteGeneration,
            [initial with { GenerationId = incompleteGeneration, Faces = initial.Faces.Select(face => face with { UnicodeCoverage = [] }).ToArray() }],
            CancellationToken.None);
        await repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = incompleteGeneration, Status = AnalysisBatchStatus.Complete, SummaryJson = "{\"faceScores\":[]}" }, CancellationToken.None);

        await StageAndPublishAsync(scanner, repository);
        Assert.Equal(0, repository.PerformanceSamples[^1].ReusedCount);
        Assert.NotEmpty(Assert.Single(Assert.Single(repository.PublishedFiles).Faces).UnicodeCoverage);

        await StageAndPublishAsync(scanner, repository);
        Assert.Equal(1, repository.PerformanceSamples[^1].ReusedCount);
    }

    [Fact]
    public async Task RefreshAsync_ShouldDiscoverOtcAndPublishUnclassifiableTerminalRecord()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Join(_root, "broken.otc");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4], TestContext.Current.CancellationToken);
        var repository = new InMemoryFontRepository();
        using FontInventoryScanner scanner = CreateScanner(repository);

        await StageAndPublishAsync(scanner, repository);

        FontFileRecord record = Assert.Single(repository.PublishedFiles);
        Assert.Equal(path, record.Path);
        Assert.Equal(FontFileHealth.Unclassifiable, record.Health);
    }

    [Fact]
    public async Task RefreshAsync_ShouldReinspectUnclassifiablePhysicalFileEvenWhenUnchanged()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllBytesAsync(Path.Join(_root, "unchanged.ttf"), [1, 2, 3, 4], TestContext.Current.CancellationToken);
        var repository = new InMemoryFontRepository();
        using FontInventoryScanner scanner = CreateScanner(repository);
        await StageAndPublishAsync(scanner, repository);

        await StageAndPublishAsync(scanner, repository);

        Assert.Equal(0, repository.PerformanceSamples[^1].ReusedCount);
    }

    [Fact]
    public async Task RefreshAsync_ShouldStoreHardLinkedPathsAsOnePhysicalRecordWithAliases()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllBytesAsync(Path.Join(_root, "first.ttf"), [1, 2, 3, 4], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(Path.Join(_root, "second.ttf"), [1, 2, 3, 4], TestContext.Current.CancellationToken);
        var repository = new InMemoryFontRepository();
        using var scanner = new FontInventoryScanner(
            repository,
            () => new FontDirectoryPolicyResolver(_root, null, null, includePlatformDefaults: false),
            new ConstantIdentityProvider(),
            NullLogger<FontInventoryScanner>.Instance);

        await StageAndPublishAsync(scanner, repository);

        FontFileRecord physicalFile = Assert.Single(repository.PublishedFiles);
        Assert.Single(physicalFile.PathAliases);
    }

    [Fact]
    public async Task RefreshAsync_ShouldRetainPreviousGenerationWhenCancelledBeforePublication()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Join(_root, "broken.ttf");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4], TestContext.Current.CancellationToken);
        var repository = new InMemoryFontRepository();
        long initialGeneration = await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], TestContext.Current.CancellationToken);
        await repository.WriteInventoryAsync(initialGeneration, [ExistingRecord(initialGeneration)], TestContext.Current.CancellationToken);
        await repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = initialGeneration, Status = AnalysisBatchStatus.Complete, SummaryJson = "{\"faceScores\":[]}" }, TestContext.Current.CancellationToken);
        using FontInventoryScanner scanner = CreateScanner(repository);
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress<double>(value =>
        {
            if (value >= 90)
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scanner.StageAsync(progress, cancellationToken: cancellation.Token));

        Assert.Equal("existing.ttf", Assert.Single(repository.PublishedFiles).Path);
    }

    private static async Task StageAndPublishAsync(FontInventoryScanner scanner, InMemoryFontRepository repository)
    {
        FontInventoryScanResult scan = await scanner.StageAsync(cancellationToken: TestContext.Current.CancellationToken);
        if (!scan.HasChanges) return;
        long generationId = scan.GenerationId;
        await repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = generationId, Status = AnalysisBatchStatus.Complete, SummaryJson = "{}" }, TestContext.Current.CancellationToken);

    }

    private FontInventoryScanner CreateScanner(InMemoryFontRepository repository)
    {
        return new FontInventoryScanner(
            repository,
            () => new FontDirectoryPolicyResolver(_root, null, null, includePlatformDefaults: false),
            new PathPhysicalFileIdentityProvider(),
            NullLogger<FontInventoryScanner>.Instance);
    }

    private static FontFileRecord ExistingRecord(long generationId)
    {
        return new FontFileRecord
        {
            GenerationId = generationId,
            Path = "existing.ttf",
            NormalizedPath = "existing.ttf",
            PhysicalIdentity = "existing.ttf",
            Sha256 = new byte[32],
            Health = FontFileHealth.Healthy,
            Faces = []
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class ConstantIdentityProvider : IPhysicalFileIdentityProvider
    {
        public string GetIdentity(string path) => "same-physical-file";
    }
}
