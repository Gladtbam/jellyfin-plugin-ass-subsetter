using System.Security.Cryptography;
using BenchmarkDotNet.Attributes;
using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.Core;
using Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;
using Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;
using Jellyfin.Plugin.AssSubsetter.Models;
using Jellyfin.Plugin.AssSubsetter.Native;
using Jellyfin.Plugin.AssSubsetter.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.AssSubsetter.Benchmarks;

/// <summary>Compares direct file/native subsetting with the production memory-index and file-lease path.</summary>
[MemoryDiagnoser]
public class LeasedSubsettingPathBenchmarks
{
    private string _root = null!;
    private string _fontPath = null!;
    private FontDatabase _database = null!;
    private FontInventoryScanner _scanner = null!;
    private FontCacheManager _manager = null!;
    private FontDescriptor _request = null!;
    private uint[] _codepoints = null!;
    private string _assPath = null!;
    private AssProcessor _directProcessor = null!;
    private AssProcessor _catalogProcessor = null!;

    /// <summary>Creates one valid deterministic font and a published warm runtime index outside timed operations.</summary>
    /// <returns>A task representing setup and output-equivalence proof.</returns>
    [GlobalSetup]
    public async Task SetupAsync()
    {
        _root = Path.Combine(Path.GetTempPath(), "ass-subsetter-path-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _fontPath = Path.Combine(_root, "fixture.ttf");
        byte[] bytes = DeterministicSubsetFont.Create();
        await File.WriteAllBytesAsync(_fontPath, bytes).ConfigureAwait(false);
        var paths = new FontStoragePaths(_root, Path.Combine(_root, "ass-subsetter.db"), Path.Combine(_root, "quarantine"), Path.Combine(_root, "manifests"));
        _database = new FontDatabase(paths);
        var repository = new SqliteFontRepository(_database);
        await repository.InitializeAsync(CancellationToken.None).ConfigureAwait(false);
        RuntimeFontFace face = SyntheticInventory.CreateFaces()[12345];
        _request = new FontDescriptor(face.FamilyName, null, false, false);
        _codepoints = [32, 33, 34, 35];
        var policy = new FontDirectoryPolicyResolver(_root, null, null, false);
        var file = new FontFileRecord
        {
            Path = _fontPath,
            NormalizedPath = Path.GetFullPath(_fontPath).ToUpperInvariant(),
            PhysicalIdentity = _fontPath,
            Size = bytes.Length,
            ModifiedUtc = File.GetLastWriteTimeUtc(_fontPath),
            Sha256 = SHA256.HashData(bytes),
            Health = FontFileHealth.Healthy,
            Faces = [new FontFaceRecord
            {
                FaceIndex = 0,
                StrictFingerprint = new byte[32],
                FamilyName = face.FamilyName,
                StyleName = face.StyleName,
                Names = face.Names,
                Weight = face.Weight,
                Width = face.Width,
                Slant = face.Slant,
                IsItalic = face.IsItalic,
                IsFixedPitch = face.IsFixedPitch,
                Technology = face.Technology
            }]
        };
        // This synthetic catalog exercises lookup and leases without running deep analysis.
        FontAnalysisInputSnapshot input = FontInventoryInputComparer.Capture([file], policy, "leased-subsetting-fixture-v1");
        long generationId = await repository.BeginGenerationAsync(input, ["benchmark-fixture"], CancellationToken.None).ConfigureAwait(false);
        await repository.WriteInventoryAsync(generationId, [file with { GenerationId = generationId }], CancellationToken.None).ConfigureAwait(false);
        await repository.PublishAnalysisAsync(new AnalysisBatch
        {
            GenerationId = generationId,
            Status = AnalysisBatchStatus.Complete,
            EvidenceVersion = FontDuplicateAnalyzer.CurrentEvidenceVersion,
            StartedUtc = DateTimeOffset.UtcNow,
            CompletedUtc = DateTimeOffset.UtcNow,
            SummaryJson = "{\"faceScores\":[]}"
        }, CancellationToken.None).ConfigureAwait(false);
        _scanner = new FontInventoryScanner(repository, () => policy, new PathPhysicalFileIdentityProvider(), NullLogger<FontInventoryScanner>.Instance);
        _manager = new FontCacheManager(repository, _scanner, new FontMaintenanceCoordinator());
        await _manager.LoadPublishedSnapshotAsync(restoreFencesAsync: null, CancellationToken.None).ConfigureAwait(false);

        _assPath = Path.Combine(_root, "fixture.ass");
        string ass = "[V4+ Styles]\nFormat: Name, Fontname, Fontsize\nStyle: Default," + face.FamilyName
            + ",20\n[Events]\nFormat: Layer, Start, End, Style, Text\nDialogue: 0,0:00:00.00,0:00:01.00,Default,!";
        await File.WriteAllTextAsync(_assPath, ass).ConfigureAwait(false);
        var configuration = new PluginConfiguration();
        var parser = new AssDocumentParser();
        _directProcessor = new AssProcessor(
            () => configuration,
            (_, _, _) => Task.FromResult<FontFileLease?>(new FontFileLease(_fontPath, 0, () => ValueTask.CompletedTask)),
            () => "BENCHMARK",
            parser,
            NullLogger<AssProcessor>.Instance);
        _catalogProcessor = new AssProcessor(
            () => configuration,
            _manager.AcquireFontAsync,
            () => "BENCHMARK",
            parser,
            NullLogger<AssProcessor>.Instance);

        byte[] direct = await DirectFileReadAndSubsetAsync().ConfigureAwait(false);
        byte[] catalog = await CatalogLeaseReadAndSubsetAsync().ConfigureAwait(false);
        if (direct.Length == 0 || !direct.AsSpan().SequenceEqual(catalog))
        {
            throw new InvalidOperationException("Direct and leased font subsetting must produce identical nonempty bytes.");
        }

        SubsetArtifact? directArtifact = await DirectAssProcessorArtifactAsync().ConfigureAwait(false);
        SubsetArtifact? catalogArtifact = await CatalogAssProcessorArtifactAsync().ConfigureAwait(false);
        if (directArtifact is null || catalogArtifact is null
            || directArtifact.AssContent != catalogArtifact.AssContent
            || directArtifact.Fonts.Count != 1 || catalogArtifact.Fonts.Count != 1
            || !directArtifact.Fonts[0].Data.AsSpan().SequenceEqual(catalogArtifact.Fonts[0].Data))
        {
            throw new InvalidOperationException("Direct and catalog AssProcessor paths must produce byte-identical nonempty artifacts.");
        }
    }

    /// <summary>Reads and subsets a known direct file path without a catalog lookup or lease.</summary>
    /// <returns>The real native subset bytes.</returns>
    [Benchmark]
    public Task<byte[]> DirectFileReadAndSubsetAsync() => SubsetAsync(_fontPath, 0);

    /// <summary>Resolves through the published runtime index, leases the file, then reads and subsets it.</summary>
    /// <returns>The same real native subset bytes.</returns>
    [Benchmark]
    public async Task<byte[]> CatalogLeaseReadAndSubsetAsync()
    {
        FontFileLease lease = await _manager.AcquireFontAsync(_request, Array.Empty<uint>(), CancellationToken.None).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The warm runtime index did not resolve the fixture font.");
        await using var leaseScope = lease.ConfigureAwait(false);
        return await SubsetAsync(lease.Path, lease.FaceIndex).ConfigureAwait(false);
    }

    /// <summary>Runs the complete artifact pipeline with direct physical-file resolution.</summary>
    /// <returns>The actual ASS content and subset font attachment.</returns>
    [Benchmark(Baseline = true)]
    public Task<SubsetArtifact?> DirectAssProcessorArtifactAsync()
        => _directProcessor.GenerateSubsetArtifactAsync(_assPath);

    /// <summary>Runs the same complete artifact pipeline with the published runtime index and lease.</summary>
    /// <returns>The byte-identical ASS content and subset font attachment.</returns>
    [Benchmark]
    public Task<SubsetArtifact?> CatalogAssProcessorArtifactAsync()
        => _catalogProcessor.GenerateSubsetArtifactAsync(_assPath);

    /// <summary>Releases setup resources and removes only the unique benchmark fixture directory.</summary>
    /// <returns>A task representing cleanup.</returns>
    [GlobalCleanup]
    public async Task CleanupAsync()
    {
        _manager?.Dispose();
        _scanner?.Dispose();
        if (_database is not null)
        {
            await _database.DisposeAsync().ConfigureAwait(false);
        }

        if (_root is not null && Path.GetFileName(_root).StartsWith("ass-subsetter-path-bench-", StringComparison.Ordinal)
            && Path.GetFullPath(_root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private async Task<byte[]> SubsetAsync(string path, int faceIndex)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        byte[]? subset = HarfBuzzSubsetNative.SubsetFont(bytes, checked((uint)faceIndex), _codepoints, NullLogger.Instance);
        return subset is { Length: > 0 } ? subset : throw new InvalidOperationException("Native font subsetting returned no bytes.");
    }
}
