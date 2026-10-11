using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

/// <summary>Plans deterministic filenames from typed identity evidence and executes recoverable moves.</summary>
public sealed class FontFilenameNormalizer : IFontFilenameNormalizationExecutor, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IFontRepository _repository;
    private readonly FontMaintenanceCoordinator _coordinator;
    private readonly IQuarantineFileSystem _fileSystem;
    private readonly FileRenameManifestStore _manifests;
    private readonly Func<FontDirectoryPolicyResolver> _policy;
    private readonly IPhysicalFileIdentityProvider _identity;
    private readonly FontRenameCandidateService _candidates;
    private readonly IFontRenameDirectoryOperations _directories;
    private readonly FontRenameDirectoryJournal _directoryJournal;
    private readonly Func<CancellationToken, Task>? _recoverQuarantine;
    private readonly SemaphoreSlim _executionGate = new(1, 1);

    /// <summary>Initializes a new instance of the <see cref="FontFilenameNormalizer"/> class.</summary>
    /// <param name="repository">The durable catalog and authorization repository.</param>
    /// <param name="coordinator">The shared runtime lease coordinator.</param>
    /// <param name="fileSystem">The no-overwrite filesystem boundary.</param>
    /// <param name="manifests">The durable immutable intent and journal store.</param>
    /// <param name="policy">Creates current directory safety policy.</param>
    /// <param name="identity">Reads current physical-file identity.</param>
    /// <param name="candidates">The current candidate authorization service.</param>
    /// <param name="directories">The physical directory and filesystem boundary.</param>
    /// <param name="recoverQuarantine">Reimports existing quarantine history before shared request identifiers are allocated.</param>
    public FontFilenameNormalizer(IFontRepository repository, FontMaintenanceCoordinator coordinator, IQuarantineFileSystem fileSystem, FileRenameManifestStore manifests, Func<FontDirectoryPolicyResolver> policy, IPhysicalFileIdentityProvider identity, FontRenameCandidateService candidates, IFontRenameDirectoryOperations directories, Func<CancellationToken, Task>? recoverQuarantine = null)
    {
        _repository = repository;
        _coordinator = coordinator;
        _fileSystem = fileSystem;
        _manifests = manifests;
        _policy = policy;
        _identity = identity;
        _candidates = candidates;
        _directories = directories;
        _directoryJournal = new FontRenameDirectoryJournal(directories, coordinator, policy, PersistAsync);
        _recoverQuarantine = recoverQuarantine;
    }

    /// <summary>Releases the serialized lifecycle execution gate.</summary>
    public void Dispose() => _executionGate.Dispose();

    /// <inheritdoc/>
    public Task<OperationRequest?> CreateCurrentRequestAsync(CancellationToken cancellationToken)
        => _candidates.CreateScheduledRequestAsync(cancellationToken);

    /// <summary>Executes or resumes one immutable authorized filename request without overwriting files.</summary>
    /// <param name="request">The durable administrator-authorized request.</param>
    /// <param name="cancellationToken">Stops before later file transitions.</param>
    /// <param name="progress">Optional progress across files and finalization.</param>
    /// <returns>The durable batch and its per-file outcomes.</returns>
    public Task<MaintenanceBatch> ExecuteAsync(OperationRequest request, CancellationToken cancellationToken, IProgress<double>? progress = null) => StartAsync(request, false, cancellationToken, progress);

    /// <summary>Rolls back selected completed rename items, skipping occupied original paths.</summary>
    /// <param name="request">The durable rollback authorization.</param>
    /// <param name="cancellationToken">Stops before later file transitions.</param>
    /// <param name="progress">Optional progress across files and finalization.</param>
    /// <returns>The durable rollback batch.</returns>
    public Task<MaintenanceBatch> RollbackAsync(OperationRequest request, CancellationToken cancellationToken, IProgress<double>? progress = null) => StartAsync(request, true, cancellationToken, progress);

    /// <summary>Reimports manifests and reconciles interrupted transitions after restart or catalog replacement.</summary>
    /// <param name="cancellationToken">Cancels between durable batches.</param>
    /// <returns>A task representing recovery and pending runtime refresh.</returns>
    public async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReconcileCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _executionGate.Release();
        }
    }

    private async Task ReconcileCoreAsync(CancellationToken cancellationToken)
    {
        if (_recoverQuarantine is not null)
        {
            await _recoverQuarantine(cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<RenameManifest> manifests = await _manifests.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        foreach (RenameManifest manifest in manifests)
        {
            await _repository.ImportMaintenanceBatchAsync(WithSummary(manifest).Batch, manifest.Request.Kind == FontOperationKind.NormalizeNames ? FontOperationRequestPayloadCodec.ParseRenameExecution(manifest.Request.RequestJson).AnalysisBatchId : JsonSerializer.Deserialize<FontOperationRequestPayload>(manifest.Request.RequestJson, JsonOptions)!.BatchId, manifest.Request.IdempotencyKey, manifest.Request.RequestJson, cancellationToken).ConfigureAwait(false);
        }

        // Reserve every historical request identity before a refresh can allocate a new analysis request.
        foreach (RenameManifest manifest in manifests)
        {
            if (manifest.RefreshRequired || manifest.Batch.Status == MaintenanceBatchStatus.Running || manifest.Directories.Any(record => record.Status is RenameDirectoryStatus.Planned or RenameDirectoryStatus.Warning))
            {
                await RunAsync(manifest, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<MaintenanceBatch> StartAsync(OperationRequest request, bool rollback, CancellationToken cancellationToken, IProgress<double>? progress)
    {
        progress?.Report(0);
        await _executionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await StartCoreAsync(request, rollback, cancellationToken, progress).ConfigureAwait(false);
        }
        finally
        {
            _executionGate.Release();
        }
    }

    private async Task<MaintenanceBatch> StartCoreAsync(OperationRequest request, bool rollback, CancellationToken cancellationToken, IProgress<double>? progress)
    {
        if (request.Id <= 0 || request.Kind != (rollback ? FontOperationKind.RollbackNames : FontOperationKind.NormalizeNames))
        {
            throw new ArgumentException("A persisted filename operation is required.", nameof(request));
        }

        OperationRequest? persisted = await _repository.GetOperationRequestAsync(request.Id, cancellationToken).ConfigureAwait(false);
        if (persisted is null || persisted.Kind != request.Kind || persisted.RequestJson != request.RequestJson || persisted.IdempotencyKey != request.IdempotencyKey)
        {
            throw new InvalidOperationException("Filename authorization does not match the persisted request.");
        }

        RenameManifest? existing = (await _manifests.LoadAllAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(manifest => manifest.Request.Id == request.Id);
        if (existing is not null)
        {
            return await RunAsync(existing, cancellationToken, progress).ConfigureAwait(false);
        }

        IReadOnlyList<RenameItemRecord> items;
        FontNamingRules rules;
        string ruleFingerprint;
        if (rollback)
        {
            FontOperationRequestPayload payload = JsonSerializer.Deserialize<FontOperationRequestPayload>(request.RequestJson, JsonOptions)
                ?? throw new InvalidOperationException("Invalid rollback authorization.");
            MaintenanceBatch original = await _repository.GetMaintenanceBatchAsync(payload.BatchId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The rename batch is absent.");
            if (original.Kind != FontOperationKind.NormalizeNames)
            {
                throw new InvalidOperationException("Rollback requires a filename execution batch.");
            }

            RenameManifest originalManifest = (await _manifests.LoadAllAsync(cancellationToken).ConfigureAwait(false)).Single(manifest => manifest.Batch.Id == original.Id);
            rules = originalManifest.Rules;
            ruleFingerprint = originalManifest.RuleFingerprint;
            items = payload.SelectedFileIds.Select(id => original.RenameItems.Single(item => item.FileId == id && item.Status == RenameItemStatus.Renamed) with { Status = RenameItemStatus.Pending, Error = null }).ToArray();
        }
        else
        {
            FontRenameExecutionPayload payload = FontOperationRequestPayloadCodec.ParseRenameExecution(request.RequestJson);
            if (!await _candidates.IsCurrentAsync(payload, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The saved filename authorization no longer matches current analysis, inventory or rules.");
            }

            rules = payload.Rules;
            ruleFingerprint = payload.RuleFingerprint;
            items = payload.Items;
        }

        MaintenanceBatch batch = await _repository.GetMaintenanceBatchByOperationRequestAsync(request.Id, cancellationToken).ConfigureAwait(false)
            ?? await _repository.SaveMaintenanceBatchAsync(new MaintenanceBatch { OperationRequestId = request.Id, Kind = request.Kind, Status = MaintenanceBatchStatus.Running, StartedUtc = DateTimeOffset.UtcNow, SummaryJson = "{}" }, cancellationToken).ConfigureAwait(false);
        batch = batch with { RenameItems = items.Select(item => item with { TemporaryPath = Path.Combine(Path.GetDirectoryName(item.OldPath)!, ".ass-subsetter-rename-" + batch.Id.ToString(CultureInfo.InvariantCulture) + "-" + item.FileId.ToString(CultureInfo.InvariantCulture) + ".tmp") }).ToArray() };
        var created = new RenameManifest(request, batch, false) { Rules = rules, RuleFingerprint = ruleFingerprint };
        await PersistAsync(created).ConfigureAwait(false);
        return await RunAsync(created, cancellationToken, progress).ConfigureAwait(false);
    }

    private async Task<MaintenanceBatch> RunAsync(RenameManifest manifest, CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        bool rollback = manifest.Batch.Kind == FontOperationKind.RollbackNames;
        manifest = await _directoryJournal.ReplayCleanupAsync(manifest).ConfigureAwait(false);
        if (manifest.Batch.Status == MaintenanceBatchStatus.Complete && !manifest.RefreshRequired)
        {
            manifest = WithSummary(manifest);
            await PersistAsync(manifest).ConfigureAwait(false);
            return manifest.Batch;
        }

        progress?.Report(5);
        bool cancelled = false;
        // Progress advances only after an item reaches a persisted result, including safe skips.
        for (int index = 0; index < manifest.Batch.RenameItems.Count; index++, progress?.Report(5 + (index * 85d / manifest.Batch.RenameItems.Count)))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            RenameItemRecord item = manifest.Batch.RenameItems[index];
            if (item.Status is not (RenameItemStatus.Pending or RenameItemStatus.Moving or RenameItemStatus.Temporary or RenameItemStatus.RollingBack))
            {
                continue;
            }

            string source = rollback ? item.NewPath : item.OldPath;
            string destination = rollback ? item.OldPath : item.NewPath;
            string? safety = PathSafetyReason(item, source, destination) ?? SafetyReason(item.Evidence, source, checkExists: false);
            if (safety is not null)
            {
                manifest = WithItem(manifest, index, item with { Status = safety == "in-use" ? RenameItemStatus.InUse : RenameItemStatus.Skipped, Error = safety });
                await PersistAsync(manifest).ConfigureAwait(false);
                continue;
            }

            using var leaseTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            leaseTimeout.CancelAfter(TimeSpan.FromMilliseconds(100));
            IAsyncDisposable lease;
            try
            {
                lease = await _coordinator.AcquireExclusiveAsync([source, destination, item.TemporaryPath], leaseTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }
            catch (OperationCanceledException)
            {
                manifest = WithItem(manifest, index, item with { Status = RenameItemStatus.InUse, Error = "lease-timeout-or-cancellation" });
                await PersistAsync(manifest).ConfigureAwait(false);
                continue;
            }

            await using (lease.ConfigureAwait(false))
            {
                try
                {
                    if (item.Status != RenameItemStatus.Pending && !ExactExists(source) && await MatchesAsync(item, destination).ConfigureAwait(false))
                    {
                        _coordinator.SetRenamePathAvailable(source, false);
                        _coordinator.SetRenamePathAvailable(destination, true);
                        item = item with { Status = rollback ? RenameItemStatus.RolledBack : RenameItemStatus.Renamed };
                        manifest = WithItem(manifest with { RefreshRequired = true }, index, item);
                    }
                    else
                    {
                        string current = item.Status != RenameItemStatus.Pending && _fileSystem.FileExists(item.TemporaryPath) ? item.TemporaryPath : source;
                        if (current == item.TemporaryPath)
                        {
                            manifest = manifest with { RefreshRequired = true };
                        }

                        if (!await MatchesAsync(item, current).ConfigureAwait(false))
                        {
                            item = item with { Status = RenameItemStatus.EvidenceDrift, Error = "source-evidence-changed" };
                        }
                        else if (Occupied(destination) && !(current == source && IsCaseOnly(source, destination)))
                        {
                            item = item with { Status = RenameItemStatus.DestinationOccupied, Error = "destination-occupied" };
                        }
                        else
                        {
                            FontRenameDirectoryProbe probe = _directories.Probe(current, item.SourceRoot, Path.GetDirectoryName(destination)!);
                            if (!probe.IsSafe || !probe.IsSameFileSystem)
                            {
                                item = item with { Status = RenameItemStatus.Skipped, Error = probe.Error ?? "different-filesystem" };
                                manifest = WithItem(manifest, index, item);
                                await PersistAsync(manifest).ConfigureAwait(false);
                                continue;
                            }

                            (manifest, string? directoryError) = await _directoryJournal.EnsureParentAsync(manifest, destination, item.SourceRoot).ConfigureAwait(false);
                            if (directoryError is not null)
                            {
                                item = item with { Status = RenameItemStatus.FailedIo, Error = directoryError };
                                manifest = WithItem(manifest, index, item);
                                await PersistAsync(manifest).ConfigureAwait(false);
                                continue;
                            }

                            if (cancellationToken.IsCancellationRequested)
                            {
                                cancelled = true;
                                break;
                            }

                            _coordinator.SetRenamePathAvailable(source, false);
                            item = item with { Status = rollback ? RenameItemStatus.RollingBack : RenameItemStatus.Moving };
                            manifest = WithItem(manifest, index, item);
                            await PersistAsync(manifest).ConfigureAwait(false);
                            if (current == source && IsCaseOnly(source, destination))
                            {
                                if (Occupied(item.TemporaryPath))
                                {
                                    throw new IOException("The generated temporary filename is occupied.");
                                }

                                await _fileSystem.MoveAsync(source, item.TemporaryPath, CancellationToken.None).ConfigureAwait(false);
                                current = item.TemporaryPath;
                                item = item with { Status = RenameItemStatus.Temporary };
                                manifest = WithItem(manifest with { RefreshRequired = true }, index, item);
                                await PersistAsync(manifest).ConfigureAwait(false);
                            }

                            if (!await MatchesAsync(item, current).ConfigureAwait(false))
                            {
                                throw new IOException("The file changed before its final move.");
                            }

                            probe = _directories.Probe(current, item.SourceRoot, Path.GetDirectoryName(destination)!);
                            if (!probe.IsSafe || !probe.IsSameFileSystem || PathSafetyReason(item, current, destination) is not null)
                            {
                                throw new IOException(probe.Error ?? "unsafe-target-directory");
                            }

                            await _fileSystem.MoveAsync(current, destination, CancellationToken.None).ConfigureAwait(false);
                            manifest = manifest with { RefreshRequired = true };
                            _coordinator.SetRenamePathAvailable(destination, true);
                            item = item with { Status = rollback ? RenameItemStatus.RolledBack : RenameItemStatus.Renamed };
                        }

                        manifest = WithItem(manifest, index, item);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    item = item with { Status = item.Status == RenameItemStatus.Pending ? RenameItemStatus.FailedIo : item.Status, Error = exception.Message };
                    manifest = WithItem(manifest, index, item);
                }

                if (await MatchesAsync(item, source).ConfigureAwait(false))
                {
                    _coordinator.SetRenamePathAvailable(source, true);
                }

                await PersistAsync(manifest).ConfigureAwait(false);
            }
        }

        if (!cancelled)
        {
            progress?.Report(90);
        }

        if (manifest.Rules.RemoveEmptyDirectories)
        {
            foreach (RenameItemRecord completed in manifest.Batch.RenameItems.Where(item => item.Status == (rollback ? RenameItemStatus.RolledBack : RenameItemStatus.Renamed)))
            {
                manifest = await _directoryJournal.CleanupAncestorsAsync(manifest, rollback ? completed.NewPath : completed.OldPath, completed.SourceRoot).ConfigureAwait(false);
            }
        }

        manifest = await _directoryJournal.CleanupCreatedAsync(manifest, cancelled).ConfigureAwait(false);
        if (!cancelled)
        {
            progress?.Report(95);
        }

        if (manifest.RefreshRequired)
        {
            IReadOnlyList<FontFileRecord> published = await _repository.LoadPublishedInventoryAsync(CancellationToken.None).ConfigureAwait(false);
            foreach (long generationId in manifest.Batch.RenameItems.Select(item => item.Evidence.GenerationId).Concat(published.Select(file => file.GenerationId)).Distinct())
            {
                await _repository.MarkAnalysisStaleAsync(generationId, CancellationToken.None).ConfigureAwait(false);
            }

            await new FontOperationRequestStore(_repository, TimeProvider.System).CreateTaskAsync(FontOperationKind.Analyze, "rename-refresh:" + manifest.Batch.Id.ToString(CultureInfo.InvariantCulture), CancellationToken.None).ConfigureAwait(false);
            manifest = manifest with { RefreshRequired = false };
        }

        bool complete = manifest.Batch.RenameItems.All(item => item.Status == (rollback ? RenameItemStatus.RolledBack : RenameItemStatus.Renamed));
        bool interrupted = manifest.Batch.RenameItems.Any(item => item.Status is RenameItemStatus.Moving or RenameItemStatus.Temporary or RenameItemStatus.RollingBack);
        manifest = manifest with { Batch = manifest.Batch with { Status = interrupted ? MaintenanceBatchStatus.Running : cancelled ? MaintenanceBatchStatus.Cancelled : complete ? MaintenanceBatchStatus.Complete : MaintenanceBatchStatus.Partial, CompletedUtc = interrupted ? null : DateTimeOffset.UtcNow } };
        manifest = WithSummary(manifest);
        await PersistAsync(manifest).ConfigureAwait(false);
        if (!cancelled && !interrupted)
        {
            progress?.Report(100);
        }

        return manifest.Batch;
    }

    private string? PathSafetyReason(RenameItemRecord item, string source, string destination)
    {
        FontDirectoryPolicyResolver policy = _policy();
        return string.IsNullOrEmpty(item.SourceRoot)
            || !FontPathComparison.IsWithin(source, item.SourceRoot) || !FontPathComparison.IsWithin(destination, item.SourceRoot)
            || !FontPathComparison.Comparer.Equals(policy.GetOwningCustomRoot(source), item.SourceRoot)
            || !FontPathComparison.Comparer.Equals(policy.GetOwningCustomRoot(destination), item.SourceRoot)
            ? "source-root-changed"
            : !_fileSystem.IsPhysicalPath(source) || !_fileSystem.IsPhysicalPath(destination) ? "linked-path"
            : (policy.GetPolicy(destination) & FontDirectoryPolicy.Protected) != 0 ? "protected-or-not-custom" : null;
    }

    private async Task<bool> MatchesAsync(RenameItemRecord item, string path)
    {
        if (!_fileSystem.FileExists(path) || SafetyReason(item.Evidence, path, checkInUse: false) is not null)
        {
            return false;
        }

        QuarantineFileEvidence? evidence = await _fileSystem.ReadEvidenceAsync(path, CancellationToken.None).ConfigureAwait(false);
        return evidence is not null && evidence.Size == item.Evidence.Size && evidence.ModifiedUtc == item.Evidence.ModifiedUtc
            && evidence.Sha256.Span.SequenceEqual(item.Evidence.Sha256.Span)
            && _identity.GetIdentity(path) == item.Evidence.PhysicalIdentity;
    }

    private string? SafetyReason(FontFileRecord file, string path, bool checkExists = true, bool checkInUse = true)
    {
        FontDirectoryPolicy policy = _policy().GetPolicy(path) | file.DirectoryPolicy;
        if ((policy & FontDirectoryPolicy.Protected) != 0 || (_policy().GetPolicy(path) & FontDirectoryPolicy.Custom) == 0)
        {
            return "protected-or-not-custom";
        }

        if (file.Health != FontFileHealth.Healthy || file.Faces.Count == 0 || file.LinkKind != FontLinkKind.None || file.PathAliases.Count != 0)
        {
            return "unclassifiable-or-link";
        }

        if (checkInUse && _coordinator.IsInUse(path))
        {
            return "in-use";
        }

        if (checkExists && !_fileSystem.FileExists(path))
        {
            return "source-missing";
        }

        if (_fileSystem.FileExists(path) && _identity.GetLinkCount(path) != 1)
        {
            return "hard-link-or-unknown-link-count";
        }

        for (FileSystemInfo? info = new FileInfo(path); info is not null; info = info is FileInfo ? ((FileInfo)info).Directory : ((DirectoryInfo)info).Parent)
        {
            if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return "linked-path";
            }

            if (info is FileInfo && info.Exists && (info.Attributes & FileAttributes.ReadOnly) != 0)
            {
                return "read-only";
            }
        }

        return null;
    }

    private async Task PersistAsync(RenameManifest manifest)
    {
        manifest = WithSummary(manifest);
        await _manifests.SaveAsync(manifest, CancellationToken.None).ConfigureAwait(false);
        await _repository.UpdateMaintenanceBatchAsync(manifest.Batch, CancellationToken.None).ConfigureAwait(false);
    }

    private static RenameManifest WithSummary(RenameManifest manifest)
        => manifest with { Batch = manifest.Batch with { SummaryJson = JsonSerializer.Serialize(new { SourceBatchId = manifest.Request.Kind == FontOperationKind.NormalizeNames ? FontOperationRequestPayloadCodec.ParseRenameExecution(manifest.Request.RequestJson).AnalysisBatchId : JsonSerializer.Deserialize<FontOperationRequestPayload>(manifest.Request.RequestJson, JsonOptions)!.BatchId, manifest.RefreshRequired, manifest.Rules, manifest.RuleFingerprint, manifest.Directories }) } };

    private static RenameManifest WithItem(RenameManifest manifest, int index, RenameItemRecord item)
    {
        RenameItemRecord[] items = manifest.Batch.RenameItems.ToArray();
        items[index] = item;
        return manifest with { Batch = manifest.Batch with { RenameItems = items } };
    }

    private bool Occupied(string path) => _fileSystem.FileExists(path) || Directory.Exists(path);

    private bool ExactExists(string path) => _fileSystem.FileExists(path) && Directory.EnumerateFiles(Path.GetDirectoryName(path)!).Any(candidate => string.Equals(candidate, path, StringComparison.Ordinal));

    private static bool IsCaseOnly(string source, string destination) => OperatingSystem.IsWindows() && !string.Equals(source, destination, StringComparison.Ordinal) && string.Equals(source, destination, StringComparison.OrdinalIgnoreCase);
}
