using System.Text.Json;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Maintenance;

public sealed class FileRenameManifestStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "manifest-rename-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ImmutableRulesAndEvaluatedEvidenceCannotChange()
    {
        var store = new FileRenameManifestStore(Path.Combine(_root, "journal"));
        var intent = Manifest();
        await store.SaveAsync(intent, CancellationToken.None);
        var changed = intent.Rules with { RemoveEmptyDirectories = !intent.Rules.RemoveEmptyDirectories };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(intent with { Rules = changed, RuleFingerprint = changed.GetFingerprint() }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(intent with { Batch = intent.Batch with { RenameItems = [intent.Batch.RenameItems[0] with { NamingEvidence = new([new("name", 4, 0, "name-table", "tampered")], []) }] } }, CancellationToken.None));
    }

    [Fact]
    public async Task JournalRequiresRelatedRootBoundedPathsAndMonotonicResults()
    {
        var store = new FileRenameManifestStore(Path.Combine(_root, "journal"));
        var intent = Manifest();
        await store.SaveAsync(intent, CancellationToken.None);
        var planned = new RenameDirectoryRecord(Path.Combine(_root, "old"), _root, RenameDirectoryAction.RemoveEmpty, RenameDirectoryStatus.Planned, null, null);
        var next = intent with { Directories = [planned] };
        await store.SaveAsync(next, CancellationToken.None);
        await store.SaveAsync(next with { Directories = [planned with { Status = RenameDirectoryStatus.Completed }] }, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(next, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(intent with { Directories = [planned with { Path = Path.Combine(_root, "unrelated") }] }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(intent with { Directories = [planned with { Path = _root }] }, CancellationToken.None));
        Assert.Equal(RenameDirectoryStatus.Completed, (await store.LoadAllAsync(CancellationToken.None)).Single().Directories.Single().Status);
    }

    [Fact]
    public async Task RootEscapeAndWrongTemporaryParentAreRejected()
    {
        var store = new FileRenameManifestStore(Path.Combine(_root, "journal"));
        var intent = Manifest();
        var item = intent.Batch.RenameItems.Single();
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(intent with { Batch = intent.Batch with { RenameItems = [item with { NewPath = Path.Combine(_root + "-other", "target.ttf") }] } }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(intent with { Batch = intent.Batch with { RenameItems = [item with { TemporaryPath = Path.Combine(_root, "bad.tmp") }] } }, CancellationToken.None));
    }

    [Fact]
    public async Task JournalStillRejectsTamperedIdentityPathsAndEvidence()
    {
        var store = new FileRenameManifestStore(Path.Combine(_root, "journal"));
        var original = Manifest();
        await store.SaveAsync(original, CancellationToken.None);
        var item = original.Batch.RenameItems.Single();
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(original with { RuleFingerprint = new string('0', 64) }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(original with { Request = original.Request with { IdempotencyKey = "changed" } }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(original with { Batch = original.Batch with { RenameItems = [item with { Evidence = item.Evidence with { Sha256 = new byte[31] } }] } }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(original with { Batch = original.Batch with { RenameItems = [item with { Evidence = item.Evidence with { PhysicalIdentity = "changed" } }] } }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(original with { Batch = original.Batch with { RenameItems = [item with { NewPath = Path.Combine(_root + "-other", "target.ttf") }] } }, CancellationToken.None));
        Assert.Equal(FontNamingRules.CurrentFormatVersion, (await store.LoadAllAsync(CancellationToken.None)).Single().Rules.FormatVersion);
    }
    private RenameManifest Manifest()
    {
        var rules = new FontNamingRules("{family}", "new", true);
        var file = FontNamingTestData.File(Path.Combine(_root, "old", "source.ttf"), FontNamingTestData.Face(names: [(1, "A")]));
        var item = new RenameItemRecord { FileId = 1, Evidence = file, SourceRoot = _root, OldPath = file.Path, NewPath = Path.Combine(_root, "new", "A.ttf"), TemporaryPath = Path.Combine(_root, "old", ".ass-subsetter-rename-1-1.tmp") };
        var request = new OperationRequest { Id = 1, Kind = FontOperationKind.NormalizeNames, IdempotencyKey = "test", RequestJson = Jellyfin.Plugin.AssSubsetter.Fonts.Persistence.FontOperationRequestPayloadCodec.CreateRenameExecutionJson(new(1, 1, rules, rules.GetFingerprint(), [item with { TemporaryPath = string.Empty }])) };
        return new RenameManifest(request, new MaintenanceBatch { Id = 1, OperationRequestId = 1, Kind = request.Kind, Status = MaintenanceBatchStatus.Running, SummaryJson = "{}", RenameItems = [item] }, false) { Rules = rules, RuleFingerprint = rules.GetFingerprint() };
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
