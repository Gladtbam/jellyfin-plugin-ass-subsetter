using System.Text.Json;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Maintenance;

public sealed class FileQuarantineManifestStoreTests
{
    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(2, 4)]
    public async Task SaveAsync_ShouldRejectUnsupportedFormatInBothStores(int version, int evidenceVersion)
    {
        string root = TemporaryRoot();
        QuarantineManifest manifest = Manifest(root) with { Version = version, EvidenceVersion = evidenceVersion };

        await Assert.ThrowsAsync<InvalidDataException>(() => new FileQuarantineManifestStore(root).SaveAsync(manifest, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => new InMemoryQuarantineManifestStore().SaveAsync(manifest, TestContext.Current.CancellationToken));

        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task SaveAsync_ShouldRejectMissingIdentityInBothStores(string? identity)
    {
        string root = TemporaryRoot();
        QuarantineManifest manifest = Manifest(root);
        manifest = manifest with { Items = [manifest.Items[0] with { SourcePhysicalIdentity = identity! }] };

        await Assert.ThrowsAsync<InvalidDataException>(() => new FileQuarantineManifestStore(root).SaveAsync(manifest, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(() => new InMemoryQuarantineManifestStore().SaveAsync(manifest, TestContext.Current.CancellationToken));

        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("evidenceVersion")]
    [InlineData("sourcePhysicalIdentity")]
    public async Task LoadAllAsync_ShouldRejectMissingRequiredFormatField(string field)
    {
        string root = TemporaryRoot();
        try
        {
            var store = new FileQuarantineManifestStore(root);
            await store.SaveAsync(Manifest(root), TestContext.Current.CancellationToken);
            string path = Path.Combine(root, "00000000000000000007", "manifest.json");
            JsonNode json = JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))!;
            JsonObject target = field == "sourcePhysicalIdentity" ? json["items"]![0]!.AsObject() : json.AsObject();
            Assert.True(target.Remove(field));
            await File.WriteAllTextAsync(path, json.ToJsonString(), TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<JsonException>(() => store.LoadAllAsync(TestContext.Current.CancellationToken));
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
    public async Task SaveAsync_ShouldRoundTripCurrentEvidenceAndOriginalPathsInBothStores()
    {
        string root = TemporaryRoot();
        try
        {
            var store = new FileQuarantineManifestStore(root);
            QuarantineManifest manifest = Manifest(root);

            await store.SaveAsync(manifest, TestContext.Current.CancellationToken);
            QuarantineManifest actual = Assert.Single(await store.LoadAllAsync(TestContext.Current.CancellationToken));

            Assert.Equal(FontDuplicateAnalyzer.CurrentEvidenceVersion, actual.EvidenceVersion);
            Assert.Equal(manifest.Items[0].SourcePath, actual.Items[0].SourcePath);
            var memory = new InMemoryQuarantineManifestStore();
            await memory.SaveAsync(manifest, TestContext.Current.CancellationToken);
            QuarantineManifest restored = Assert.Single(await memory.LoadAllAsync(TestContext.Current.CancellationToken));
            Assert.Equal(FontDuplicateAnalyzer.CurrentEvidenceVersion, restored.EvidenceVersion);
            Assert.Equal(manifest.Items[0].SourcePath, restored.Items[0].SourcePath);
            Assert.True(File.Exists(Path.Combine(root, "00000000000000000007", "manifest.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveAsync_ShouldRejectNonGeneratedDestinationPath()
    {
        string root = TemporaryRoot();
        try
        {
            var store = new FileQuarantineManifestStore(root);
            QuarantineManifest manifest = Manifest(root);
            manifest = manifest with
            {
                Items =
                [
                    manifest.Items[0] with { StagingPath = Path.Combine(root, "outside.stage") }
                ]
            };

            await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(manifest, TestContext.Current.CancellationToken));
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
    public async Task SaveAsync_ShouldSeparateManifestAndQuarantineRoots()
    {
        string root = TemporaryRoot();
        string manifestRoot = Path.Combine(root, "manifests");
        string quarantineRoot = Path.Combine(root, "quarantine");
        try
        {
            var store = new FileQuarantineManifestStore(manifestRoot, quarantineRoot);
            QuarantineManifest manifest = Manifest(quarantineRoot);

            await store.SaveAsync(manifest, TestContext.Current.CancellationToken);

            Assert.True(File.Exists(Path.Combine(manifestRoot, "00000000000000000007", "manifest.json")));
            Assert.Single(await store.LoadAllAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static QuarantineManifest Manifest(string root)
    {
        string itemRoot = Path.Combine(root, "00000000000000000007", "items");
        return new QuarantineManifest
        {
            Version = 2,
            EvidenceVersion = FontDuplicateAnalyzer.CurrentEvidenceVersion,
            BatchId = 7,
            OperationRequestId = 8,
            OperationIdempotencyKey = "quarantine-request",
            OperationRequestJson = "{\"batchId\":9,\"selectedFileIds\":[42]}",
            AnalysisBatchId = 9,
            CreatedUtc = DateTimeOffset.UnixEpoch,
            Items =
            [
                new QuarantineManifestItem
                {
                    FileId = 42,
                    SourcePath = Path.Combine(root, "..", "source.ttf"),
                    SourcePhysicalIdentity = "source-identity",
                    StagingPath = Path.Combine(itemRoot, "00000000000000000042.stage"),
                    QuarantinePath = Path.Combine(itemRoot, "00000000000000000042.font"),
                    Size = 3,
                    ModifiedUtc = DateTimeOffset.UnixEpoch,
                    Sha256 = new byte[32],
                    Replacements = [],
                    FaceReplacementProofs = [],
                    Status = QuarantineItemStatus.Pending
                }
            ]
        };
    }

    private static string TemporaryRoot()
        => Path.Combine(Path.GetTempPath(), nameof(FileQuarantineManifestStoreTests), Guid.NewGuid().ToString("N"));
}
