using BenchmarkDotNet.Attributes;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

namespace Jellyfin.Plugin.AssSubsetter.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory("Persistence")]
public class PersistenceReadBenchmarks
{
    private PersistenceFixture _fixture = null!;

    [Params(1_000, 10_000)]
    public int FileCount { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _fixture = await PersistenceFixture.CreateAsync();
        long generation = await _fixture.Repository.BeginGenerationAsync(FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await _fixture.Repository.WriteInventoryAsync(generation, PersistenceFixture.CreateFiles(FileCount), CancellationToken.None);
        await _fixture.Repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = generation, Status = AnalysisBatchStatus.Complete, SummaryJson = "{\"faceScores\":[]}" }, CancellationToken.None);
    }

    [Benchmark]
    public Task<IReadOnlyList<FontFileRecord>> Inventory() => _fixture.Repository.LoadPublishedInventoryAsync(CancellationToken.None);

    [Benchmark]
    public Task<IReadOnlyList<FontFileRecord>> InventoryWithCoverage() => _fixture.Repository.LoadPublishedInventoryForReuseAsync(CancellationToken.None);

    [Benchmark]
    public Task<IReadOnlyList<RuntimeFontFace>> RuntimeSnapshot() => _fixture.Repository.LoadPublishedRuntimeFacesAsync(CancellationToken.None);

    [GlobalCleanup]
    public async Task Cleanup() => await _fixture.DisposeAsync();
}

[MemoryDiagnoser]
[BenchmarkCategory("Persistence")]
public class PersistenceWriteBenchmarks
{
    private PersistenceFixture _fixture = null!;
    private FontFileRecord[] _files = null!;
    private long _generation;

    [Params(1_000, 10_000)]
    public int FileCount { get; set; }

    [GlobalSetup]
    public void Setup() => _files = PersistenceFixture.CreateFiles(FileCount);

    [IterationSetup]
    public void Reset()
    {
        _fixture = PersistenceFixture.CreateAsync().GetAwaiter().GetResult();
        _generation = _fixture.Repository.BeginGenerationAsync(FontAnalysisInputSnapshot.Empty, [], CancellationToken.None).GetAwaiter().GetResult();
    }

    [Benchmark]
    public Task Inventory() => _fixture.Repository.WriteInventoryAsync(_generation, _files, CancellationToken.None);

    [IterationCleanup]
    public void Cleanup() => _fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
}

[MemoryDiagnoser]
[BenchmarkCategory("Persistence")]
public class PersistencePublicationBenchmarks
{
    private PersistenceFixture _fixture = null!;
    private FontFileRecord[] _files = null!;
    private AnalysisBatch _batch = null!;

    [Params(1_000, 10_000)]
    public int FileCount { get; set; }

    [GlobalSetup]
    public void Setup() => _files = PersistenceFixture.CreateFiles(FileCount);

    [IterationSetup]
    public void Reset()
    {
        _fixture = PersistenceFixture.CreateAsync().GetAwaiter().GetResult();
        long generation = _fixture.Repository.BeginGenerationAsync(FontAnalysisInputSnapshot.Empty, [], CancellationToken.None).GetAwaiter().GetResult();
        _fixture.Repository.WriteInventoryAsync(generation, _files, CancellationToken.None).GetAwaiter().GetResult();
        var files = _fixture.Repository.LoadInventoryAsync(generation, CancellationToken.None).GetAwaiter().GetResult();
        var scores = files.SelectMany(file => file.Faces.Select(face => new FontFaceAnalysisRecord
        {
            FaceId = face.Id, FileId = file.Id, RetentionScore = 100, IsRuntimePreferred = true, ScoreJson = "{}", VerificationJson = "{}"
        })).ToArray();
        _batch = new AnalysisBatch
        {
            GenerationId = generation, Status = AnalysisBatchStatus.Complete, FaceScores = scores,
            SummaryJson = System.Text.Json.JsonSerializer.Serialize(new { faceScores = scores }, System.Text.Json.JsonSerializerOptions.Web)
        };
    }

    [Benchmark]
    public Task<AnalysisBatch> Publish() => _fixture.Repository.PublishAnalysisAsync(_batch, CancellationToken.None);

    [IterationCleanup]
    public void Cleanup() => _fixture.DisposeAsync().AsTask().GetAwaiter().GetResult();
}


internal sealed class PersistenceFixture : IAsyncDisposable
{
    private readonly string _root;
    private readonly FontDatabase _database;

    private PersistenceFixture(string root, FontDatabase database)
    {
        _root = root;
        _database = database;
        Repository = new SqliteFontRepository(database);
    }

    internal SqliteFontRepository Repository { get; }

    internal static async Task<PersistenceFixture> CreateAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "ass-subsetter-persistence-benchmarks", Guid.NewGuid().ToString("N"));
        var paths = new FontStoragePaths(root, Path.Combine(root, "catalog.db"), Path.Combine(root, "quarantine"), Path.Combine(root, "manifests"));
        var fixture = new PersistenceFixture(root, new FontDatabase(paths));
        await fixture.Repository.InitializeAsync(CancellationToken.None);
        return fixture;
    }

    internal static FontFileRecord[] CreateFiles(int count) => Enumerable.Range(0, count).Select(index => new FontFileRecord
    {
        Path = $"/synthetic/fonts/{index:D5}.ttf",
        NormalizedPath = $"/SYNTHETIC/FONTS/{index:D5}.TTF",
        PhysicalIdentity = $"file-{index:D5}",
        PathAliases = [$"/synthetic/aliases/{index:D5}.ttf"],
        Size = 128,
        ModifiedUtc = DateTimeOffset.UnixEpoch,
        Sha256 = new byte[32],
        Health = FontFileHealth.Healthy,
        Faces = [new FontFaceRecord
        {
            FaceIndex = 0, StrictFingerprint = new byte[32], FamilyName = $"Family {index:D5}", StyleName = "Regular",
            Names = [$"Family {index:D5}", $"Alias {index:D5}"], Weight = 400, Width = 5, Technology = "TrueType",
            UnicodeCoverage = [new UnicodeRange(0x20, 0x7e), new UnicodeRange(0x4e00, 0x4eff)]
        }]
    }).ToArray();

    public async ValueTask DisposeAsync()
    {
        await _database.DisposeAsync();
        Directory.Delete(_root, recursive: true);
    }
}
