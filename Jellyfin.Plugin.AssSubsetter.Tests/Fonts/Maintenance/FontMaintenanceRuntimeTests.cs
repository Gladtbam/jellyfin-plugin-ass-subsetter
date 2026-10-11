using System.Security.Cryptography;
using Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;
using Jellyfin.Plugin.AssSubsetter.Models;
using Jellyfin.Plugin.AssSubsetter.Services;
using Jellyfin.Plugin.AssSubsetter.Tests.Services;
using Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Fixtures;
using Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Maintenance;

public sealed class FontMaintenanceRuntimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AcquireFontAsync_ShouldNeverLeaseRemovedPathAfterRealQuarantine(bool selectedBeforeMutation)
    {
        string root = Path.Combine(Path.GetTempPath(), "ass-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new InMemoryFontRepository();
            long generation = await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], TestContext.Current.CancellationToken);
            var identity = new PlatformPhysicalFileIdentityProvider();
            byte[] data = GeneratedAnalysisFont.Create();
            string sourcePath = Path.Combine(root, "a.ttf");
            string retainedPath = Path.Combine(root, "b.ttf");
            await File.WriteAllBytesAsync(sourcePath, data, TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(retainedPath, data, TestContext.Current.CancellationToken);
            FontFileRecord source = Record(sourcePath, 1, generation, data, identity, false);
            FontFileRecord retained = Record(retainedPath, 2, generation, data, identity, true);
            await repository.WriteInventoryAsync(generation, [source, retained], TestContext.Current.CancellationToken);
            var coordinator = new FontMaintenanceCoordinator();
            Func<FontDirectoryPolicyResolver> policy = () => new(root, null, null, false);
            using var scanner = new FontInventoryScanner(repository, policy, identity, NullLogger<FontInventoryScanner>.Instance);
            using var manager = new FontCacheManager(repository, scanner, coordinator);
            var evidence = new PhysicalFontAnalysisEvidenceSource(repository, File.ReadAllBytesAsync,
                (_, _, _) => FontVerificationStatus.Passed,
                (_, _) => FontVerificationStatus.Passed);
            var analyzer = new FontDuplicateAnalyzer(evidence, new FontRelationshipClassifier(), new FontRetentionSelector(), TimeProvider.System);
            AnalysisBatch analysis = await analyzer.AnalyzeAsync(generation, null, TestContext.Current.CancellationToken);
            // Force the soon-to-be-removed path to lead runtime lookup so the test exercises lease re-selection.
            FontFaceAnalysisRecord[] runtimeScores = analysis.FaceScores.Select(face => face with
            {
                RetentionScore = face.FileId == source.Id ? 500 : 0,
                IsRuntimePreferred = face.FileId == source.Id,
                ScoreJson = face.FileId == source.Id ? "{\"total\":500}" : "{\"total\":0}"
            }).ToArray();
            analysis = analysis with
            {
                FaceScores = runtimeScores,
                SummaryJson = System.Text.Json.JsonSerializer.Serialize(new { faceScores = runtimeScores }, System.Text.Json.JsonSerializerOptions.Web)
            };
            analysis = await manager.PublishAnalyzedGenerationAsync(analysis, TestContext.Current.CancellationToken);
            Assert.Contains(analysis.Groups.SelectMany(group => group.Members), member => member.FileId == 1 && member.Decision == DuplicateGroupMemberDecision.QuarantineCandidate);
            var descriptor = new FontDescriptor(source.Faces[0].FamilyName, null, false, false);
            Assert.Equal(sourcePath, (await FontCacheManagerFixture.LookupAsync(manager, descriptor))?.Path);
            int initialized = repository.InitializeCallCount;
            int loaded = repository.RuntimeSnapshotLoadCallCount;
            var files = new PausingFileSystem(sourcePath, selectedBeforeMutation);
            var quarantine = new FontQuarantineManager(repository, coordinator, files, new InMemoryQuarantineManifestStore(), Path.Combine(root, "quarantine"), TimeProvider.System, policy);
            OperationRequest request = await new FontOperationRequestStore(repository, TimeProvider.System).CreateAsync(FontOperationKind.Quarantine, analysis.Id, [1], "real-runtime-quarantine", TestContext.Current.CancellationToken);
            Task<MaintenanceBatch> operation = quarantine.QuarantineAsync(request, null, TestContext.Current.CancellationToken);
            Task<FontFileLease?>? queuedLookup = null;
            if (selectedBeforeMutation)
            {
                await files.SourceReadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
                queuedLookup = manager.AcquireFontAsync(descriptor, Array.Empty<uint>(), TestContext.Current.CancellationToken);
                Assert.False(queuedLookup.IsCompleted);
                files.ReleaseSourceRead.TrySetResult();
            }

            MaintenanceBatch result = await operation;
            Assert.Equal(QuarantineItemStatus.Quarantined, Assert.Single(result.Items).Status);
            await using (FontFileLease? lease = await (queuedLookup ?? manager.AcquireFontAsync(descriptor, Array.Empty<uint>(), TestContext.Current.CancellationToken)))
            {
                Assert.NotNull(lease);
                Assert.True(File.Exists(lease.Path));
                Assert.Equal(retainedPath, lease.Path);
            }

            Assert.Equal(retainedPath, (await FontCacheManagerFixture.LookupAsync(manager, descriptor))?.Path);
            Assert.Equal(initialized, repository.InitializeCallCount);
            Assert.Equal(loaded, repository.RuntimeSnapshotLoadCallCount);
            OperationRequest restore = await new FontOperationRequestStore(repository, TimeProvider.System).CreateAsync(FontOperationKind.Restore, result.Id, [1], "real-runtime-restore", TestContext.Current.CancellationToken);
            await quarantine.RestoreAsync(restore, TestContext.Current.CancellationToken);
            await using FontFileLease? restored = await manager.AcquireFontAsync(descriptor, Array.Empty<uint>(), TestContext.Current.CancellationToken);
            Assert.NotNull(restored);
            Assert.True(File.Exists(restored.Path));
            Assert.Equal(sourcePath, restored.Path);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static FontFileRecord Record(string path, long id, long generation, byte[] data, IPhysicalFileIdentityProvider identity, bool preferred)
    {
        OpenTypeFace face = OpenTypeFontReader.Read(data).Faces[0];
        InspectedFontFace inspected = FontFaceInspector.Inspect(face);
        return new FontFileRecord
        {
            Id = id, GenerationId = generation, Path = path, NormalizedPath = path, PhysicalIdentity = identity.GetIdentity(path), Size = data.Length,
            ModifiedUtc = File.GetLastWriteTimeUtc(path), Sha256 = SHA256.HashData(data), Health = FontFileHealth.Healthy,
            DirectoryPolicy = FontDirectoryPolicy.Custom | (preferred ? FontDirectoryPolicy.Preferred : FontDirectoryPolicy.None),
            Faces = [new FontFaceRecord { Id = id, FaceIndex = 0, StrictFingerprint = FontFingerprintBuilder.BuildFace(face).StrictFingerprint, FamilyName = inspected.FamilyName,
                StyleName = inspected.StyleName, Names = inspected.Names, NameRecords = inspected.NameRecords, Technology = inspected.Technology, UnitsPerEm = inspected.UnitsPerEm,
                Weight = inspected.Weight, Width = inspected.Width }]
        };
    }

    private sealed class PausingFileSystem(string sourcePath, bool pause) : IQuarantineFileSystem
    {
        private readonly PhysicalQuarantineFileSystem _physical = new();
        private bool _pause = pause;
        public TaskCompletionSource SourceReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSourceRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FileExists(string path) => _physical.FileExists(path);
        public bool IsSameVolume(string source, string destination) => _physical.IsSameVolume(source, destination);
        public bool IsPhysicalPath(string path) => _physical.IsPhysicalPath(path);
        public Task<long> GetAvailableSpaceAsync(string path, CancellationToken token) => _physical.GetAvailableSpaceAsync(path, token);
        public Task CreateDirectoryAsync(string path, CancellationToken token) => _physical.CreateDirectoryAsync(path, token);
        public Task CopyAsync(string source, string destination, CancellationToken token) => _physical.CopyAsync(source, destination, token);
        public Task MoveAsync(string source, string destination, CancellationToken token) => _physical.MoveAsync(source, destination, token);
        public Task DeleteAsync(string path, CancellationToken token) => _physical.DeleteAsync(path, token);
        public async Task<QuarantineFileEvidence?> ReadEvidenceAsync(string path, CancellationToken token)
        {
            if (_pause && path == sourcePath)
            {
                _pause = false;
                SourceReadStarted.TrySetResult();
                await ReleaseSourceRead.Task.WaitAsync(token);
            }

            return await _physical.ReadEvidenceAsync(path, token);
        }
    }
}
