using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

/// <summary>Executes recoverable, per-file quarantine lifecycle transitions from immutable requests.</summary>
public sealed class FontQuarantineManager
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<FontRelationshipKind> AutomaticRelationshipKinds =
    [
        FontRelationshipKind.IdenticalFileHash,
        FontRelationshipKind.StrictFaceDuplicate,
        FontRelationshipKind.RenderingAndLayoutEquivalent,
        FontRelationshipKind.MetadataOnlyDifference,
        FontRelationshipKind.CoverageSuperset
    ];

    private readonly IFontRepository _repository;
    private readonly ILogger<FontQuarantineManager> _logger;
    private readonly FontMaintenanceCoordinator _coordinator;
    private readonly IQuarantineFileSystem _fileSystem;
    private readonly IQuarantineManifestStore _manifestStore;
    private readonly string _quarantineRoot;
    private readonly TimeProvider _timeProvider;
    private readonly Func<FontDirectoryPolicyResolver> _policy;

    /// <summary>Initializes a new instance of the <see cref="FontQuarantineManager"/> class.</summary>
    /// <param name="repository">The durable font repository.</param>
    /// <param name="coordinator">The runtime Font File lease coordinator.</param>
    /// <param name="fileSystem">The no-overwrite filesystem adapter.</param>
    /// <param name="manifestStore">The durable recovery-manifest store.</param>
    /// <param name="quarantineRoot">The fixed plugin-internal quarantine root.</param>
    /// <param name="timeProvider">The UTC clock used for lifecycle timestamps.</param>
    /// <param name="policy">Creates the current directory policy used to guard recovery mutations.</param>
    /// <param name="logger">Records quarantine expiry counts and blocked reasons.</param>
    public FontQuarantineManager(
        IFontRepository repository,
        FontMaintenanceCoordinator coordinator,
        IQuarantineFileSystem fileSystem,
        IQuarantineManifestStore manifestStore,
        string quarantineRoot,
        TimeProvider timeProvider,
        Func<FontDirectoryPolicyResolver> policy,
        ILogger<FontQuarantineManager>? logger = null)
    {
        _logger = logger ?? NullLogger<FontQuarantineManager>.Instance;
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _manifestStore = manifestStore ?? throw new ArgumentNullException(nameof(manifestStore));
        ArgumentException.ThrowIfNullOrWhiteSpace(quarantineRoot);
        _quarantineRoot = Path.GetFullPath(quarantineRoot);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
    }

    /// <summary>Quarantines selected physical Font Files and reports per-file progress.</summary>
    /// <param name="request">The persisted immutable quarantine request.</param>
    /// <param name="progress">Receives percentage progress across the selected files.</param>
    /// <param name="cancellationToken">A token that stops before acquiring later items.</param>
    /// <returns>The durable terminal or resumable maintenance batch.</returns>
    public async Task<MaintenanceBatch> QuarantineAsync(OperationRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Id <= 0 || request.Kind != FontOperationKind.Quarantine || !request.AnalysisBatchId.HasValue)
        {
            throw new ArgumentException("A persisted quarantine request with an analysis batch is required.", nameof(request));
        }

        FontOperationRequestPayload payload = JsonSerializer.Deserialize<FontOperationRequestPayload>(request.RequestJson, JsonOptions)
            ?? throw new InvalidOperationException("The operation request payload is invalid.");
        if (payload.BatchId != request.AnalysisBatchId.Value)
        {
            throw new InvalidOperationException("The immutable payload does not match the referenced analysis batch.");
        }

        AnalysisBatch analysis = await _repository.GetAnalysisBatchAsync(payload.BatchId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The referenced analysis batch does not exist.");
        if (analysis.Status != AnalysisBatchStatus.Complete)
        {
            throw new InvalidOperationException("Only a complete analysis batch can authorize quarantine.");
        }

        if (analysis.EvidenceVersion != FontDuplicateAnalyzer.CurrentEvidenceVersion
            || analysis.Relations.Any(relation => relation.EvidenceVersion != FontDuplicateAnalyzer.CurrentEvidenceVersion))
        {
            throw new InvalidOperationException("Unsupported analysis evidence cannot authorize quarantine; run a fresh analysis.");
        }

        IReadOnlyList<FontFileRecord> inventory = await _repository.LoadPublishedInventoryAsync(cancellationToken).ConfigureAwait(false);
        if (inventory.Count == 0 || inventory.Any(file => file.GenerationId != analysis.GenerationId))
        {
            throw new InvalidOperationException("The published inventory generation no longer matches the analysis batch.");
        }

        Dictionary<long, FontFileRecord> files = inventory.ToDictionary(file => file.Id);
        QuarantineManifest? existingManifest = (await _manifestStore.LoadAllAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(candidate => candidate.OperationRequestId == request.Id);
        MaintenanceBatch batch;
        QuarantineManifest manifest;
        if (existingManifest is null)
        {
            await EnsureSpaceAsync(files, payload.SelectedFileIds, cancellationToken).ConfigureAwait(false);
            MaintenanceBatch? existingBatch = await _repository.GetMaintenanceBatchByOperationRequestAsync(
                request.Id,
                cancellationToken).ConfigureAwait(false);
            if (existingBatch is null)
            {
                batch = await _repository.SaveMaintenanceBatchAsync(
                    new MaintenanceBatch
                    {
                        OperationRequestId = request.Id,
                        Kind = FontOperationKind.Quarantine,
                        Status = MaintenanceBatchStatus.Running,
                        StartedUtc = _timeProvider.GetUtcNow(),
                        SummaryJson = "{}"
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                batch = existingBatch;
            }

            if (batch.Kind != FontOperationKind.Quarantine)
            {
                throw new InvalidOperationException("The operation request is already assigned to a different maintenance kind.");
            }

            manifest = BuildManifest(batch, analysis, request, payload.SelectedFileIds, files);
            await _fileSystem.CreateDirectoryAsync(Path.Combine(BatchDirectory(batch.Id), "items"), cancellationToken).ConfigureAwait(false);
            await PersistAsync(batch, manifest, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            manifest = existingManifest;
            manifest.ValidateCurrentFormat();

            batch = await _repository.GetMaintenanceBatchAsync(manifest.BatchId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The resumable quarantine batch does not exist.");
            batch = batch with { Status = MaintenanceBatchStatus.Running, CompletedUtc = null };
        }

        try
        {
            progress?.Report(0);
            for (int index = 0; index < manifest.Items.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                QuarantineManifestItem item = manifest.Items[index];
                if (item.Status is not (QuarantineItemStatus.Pending or QuarantineItemStatus.Cancelled))
                {
                    progress?.Report((index + 1) * 100d / manifest.Items.Count);
                    continue;
                }

                int currentIndex = index;
                QuarantineManifestItem result = await QuarantineItemAsync(
                    batch.Id,
                    item,
                    async intermediate =>
                    {
                        manifest = ReplaceItem(manifest, currentIndex, intermediate);
                        await PersistAsync(batch, manifest, CancellationToken.None).ConfigureAwait(false);
                        double stage = intermediate.Status == QuarantineItemStatus.Staged ? 0.4 : 0.8;
                        progress?.Report((currentIndex + stage) * 100d / manifest.Items.Count);
                    },
                    cancellationToken).ConfigureAwait(false);
                manifest = ReplaceItem(manifest, index, result);
                await PersistAsync(batch, manifest, CancellationToken.None).ConfigureAwait(false);
                progress?.Report((index + 1) * 100d / manifest.Items.Count);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            batch = BatchWithManifest(batch, manifest, MaintenanceBatchStatus.Cancelled, _timeProvider.GetUtcNow());
            await PersistAsync(batch, manifest, CancellationToken.None).ConfigureAwait(false);
            return batch;
        }

        MaintenanceBatchStatus status = manifest.Items.All(item => item.Status == QuarantineItemStatus.Quarantined)
            ? MaintenanceBatchStatus.Complete
            : MaintenanceBatchStatus.Partial;
        batch = BatchWithManifest(batch, manifest, status, _timeProvider.GetUtcNow());
        await PersistAsync(batch, manifest, CancellationToken.None).ConfigureAwait(false);
        return batch;
    }

    /// <summary>Restores selected quarantined Font Files after hash verification without overwriting original paths.</summary>
    /// <param name="request">The persisted immutable restore request.</param>
    /// <param name="cancellationToken">A token that stops before acquiring later items.</param>
    /// <returns>The updated durable quarantine batch.</returns>
    public async Task<MaintenanceBatch> RestoreAsync(OperationRequest request, CancellationToken cancellationToken)
    {
        FontOperationRequestPayload payload = ReadPayload(request, FontOperationKind.Restore);
        QuarantineManifest manifest = (await _manifestStore.LoadAllAsync(cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(candidate => candidate.BatchId == payload.BatchId)
            ?? throw new InvalidOperationException("The referenced quarantine manifest does not exist.");
        manifest.ValidateCurrentFormat();
        MaintenanceBatch batch = await _repository.GetMaintenanceBatchAsync(payload.BatchId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The referenced quarantine batch does not exist.");
        HashSet<long> selected = payload.SelectedFileIds.ToHashSet();
        if (!selected.IsSubsetOf(manifest.Items.Select(item => item.FileId)))
        {
            throw new InvalidOperationException("The restore request contains a physical Font File outside the referenced manifest.");
        }

        for (int index = 0; index < manifest.Items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            QuarantineManifestItem item = manifest.Items[index];
            if (!selected.Contains(item.FileId)
                || item.Status is not (QuarantineItemStatus.Quarantined or QuarantineItemStatus.Restoring))
            {
                continue;
            }

            IAsyncDisposable lease = await _coordinator.AcquireExclusiveAsync(
                [item.SourcePath, item.QuarantinePath, RestoreStagingPath(manifest.BatchId, item)],
                cancellationToken).ConfigureAwait(false);
            await using var leaseScope = lease.ConfigureAwait(false);
            if (item.Status == QuarantineItemStatus.Restoring)
            {
                item = await ReconcileRestoreItemAsync(manifest.BatchId, item, cancellationToken).ConfigureAwait(false);
                manifest = ReplaceItem(manifest, index, item);
                await PersistAsync(batch, manifest, CancellationToken.None).ConfigureAwait(false);
                if (item.Status != QuarantineItemStatus.Quarantined)
                {
                    continue;
                }
            }

            QuarantineManifestItem result;
            if (!RecoveryPathsSafe(manifest.BatchId, item))
            {
                result = item with { Status = QuarantineItemStatus.EvidenceDrift, Error = "Current directory or path safety no longer authorizes restoration." };
            }
            else if (_fileSystem.FileExists(item.SourcePath))
            {
                result = item with
                {
                    Status = QuarantineItemStatus.DestinationOccupied,
                    Error = "The original path is occupied; restore did not overwrite it."
                };
            }
            else if (!await MatchesAsync(item.QuarantinePath, item.Size, item.Sha256, null, cancellationToken).ConfigureAwait(false))
            {
                result = item with
                {
                    Status = QuarantineItemStatus.FailedVerification,
                    Error = "The quarantine copy failed size or SHA-256 verification."
                };
            }
            else
            {
                string restoreStagingPath = RestoreStagingPath(manifest.BatchId, item);
                if (!_fileSystem.IsSameVolume(item.QuarantinePath, item.SourcePath)
                    && _fileSystem.FileExists(restoreStagingPath))
                {
                    result = item with
                    {
                        Status = QuarantineItemStatus.DestinationOccupied,
                        Error = "The generated restore staging path is already occupied."
                    };
                }
                else
                {
                    item = item with { Status = QuarantineItemStatus.Restoring, Error = null };
                    manifest = ReplaceItem(manifest, index, item);
                    await PersistAsync(batch, manifest, CancellationToken.None).ConfigureAwait(false);
                    bool restored = await RestoreFileAsync(manifest.BatchId, item, cancellationToken).ConfigureAwait(false);
                    result = restored ? Restored(item) : item with { Status = QuarantineItemStatus.EvidenceDrift, Error = "Current path safety changed during restoration." };
                }
            }

            manifest = ReplaceItem(manifest, index, result);
            await PersistAsync(batch, manifest, CancellationToken.None).ConfigureAwait(false);
        }

        MaintenanceBatchStatus status = manifest.Items
            .Where(item => selected.Contains(item.FileId))
            .All(item => item.Status == QuarantineItemStatus.Restored)
            ? MaintenanceBatchStatus.Complete
            : MaintenanceBatchStatus.Partial;
        batch = BatchWithManifest(batch, manifest, status, _timeProvider.GetUtcNow());
        await PersistAsync(batch, manifest, CancellationToken.None).ConfigureAwait(false);
        return batch;
    }

    /// <summary>Permanently removes quarantine copies older than thirty days after revalidating every replacement.</summary>
    /// <param name="cancellationToken">A token that stops before acquiring later items.</param>
    /// <param name="progress">Optional progress across inspected manifest items.</param>
    /// <returns>The number of quarantine copies permanently removed.</returns>
    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        progress?.Report(0);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset threshold = now.AddDays(-30);
        int purged = 0;
        IReadOnlyList<QuarantineManifest> manifests = await _manifestStore.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        long total = manifests.Sum(manifest => (long)manifest.Items.Count);
        long inspected = 0;
        long eligible = 0;
        var blocked = new Dictionary<string, int>(StringComparer.Ordinal);
        _logger.LogInformation("[AssSubsetter] Quarantine expiry started. Manifests: {ManifestCount}; Items: {ItemCount}; ThresholdUtc: {ThresholdUtc}.", manifests.Count, total, threshold);
        foreach (QuarantineManifest loaded in manifests)
        {
            QuarantineManifest manifest = loaded;
            manifest.ValidateCurrentFormat();
            MaintenanceBatch? batch = await _repository.GetMaintenanceBatchAsync(manifest.BatchId, cancellationToken).ConfigureAwait(false);
            if (batch is null)
            {
                inspected += manifest.Items.Count;
                progress?.Report(inspected * 100d / Math.Max(1, total));
                blocked["maintenance-batch-missing"] = blocked.GetValueOrDefault("maintenance-batch-missing") + manifest.Items.Count;
                continue;
            }

            // A failed or cancelled transition leaves this item outside the processed count.
            for (int index = 0; index < manifest.Items.Count; index++, inspected++, progress?.Report(inspected * 100d / Math.Max(1, total)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                QuarantineManifestItem item = manifest.Items[index];
                if (item.Status != QuarantineItemStatus.Quarantined
                    || !item.QuarantinedUtc.HasValue
                    || item.QuarantinedUtc.Value > threshold)
                {
                    continue;
                }

                eligible++;
                IAsyncDisposable lease = await _coordinator.AcquireExclusiveAsync(
                    item.Replacements.Select(replacement => replacement.Path).Append(item.QuarantinePath),
                    cancellationToken).ConfigureAwait(false);
                await using var leaseScope = lease.ConfigureAwait(false);
                if (!PurgePathsSafe(manifest.BatchId, item))
                {
                    manifest = ReplaceItem(manifest, index, item with
                    {
                        Error = "Expiry was blocked because current directory or path safety no longer authorizes deletion."
                    });
                    await PersistAsync(batch, manifest, CancellationToken.None).ConfigureAwait(false);
                    blocked["unsafe-path"] = blocked.GetValueOrDefault("unsafe-path") + 1;
                    continue;
                }

                bool replacementsValid = FaceProofsValid(item);
                foreach (QuarantineReplacementEvidence replacement in item.Replacements)
                {
                    if (!await MatchesAsync(replacement.Path, replacement.Size, replacement.Sha256, null, cancellationToken).ConfigureAwait(false))
                    {
                        replacementsValid = false;
                        break;
                    }
                }

                QuarantineManifestItem result;
                if (!replacementsValid)
                {
                    result = item with
                    {
                        Status = QuarantineItemStatus.ReplacementMissing,
                        Error = "Expiry was blocked because a retained replacement is missing or changed."
                    };
                }
                else if (!await MatchesAsync(item.QuarantinePath, item.Size, item.Sha256, null, cancellationToken).ConfigureAwait(false))
                {
                    result = item with
                    {
                        Status = QuarantineItemStatus.FailedVerification,
                        Error = "Expiry was blocked because the quarantine copy failed verification."
                    };
                }
                else if (!PurgePathsSafe(manifest.BatchId, item))
                {
                    result = item with
                    {
                        Error = "Expiry was blocked because current directory or path safety changed during verification."
                    };
                }
                else
                {
                    await _fileSystem.DeleteAsync(item.QuarantinePath, cancellationToken).ConfigureAwait(false);
                    result = item with { Status = QuarantineItemStatus.Purged, Error = null };
                    purged++;
                }

                if (result.Error is not null)
                {
                    blocked[result.Error] = blocked.GetValueOrDefault(result.Error) + 1;
                }

                manifest = ReplaceItem(manifest, index, result);
                await PersistAsync(batch, manifest, CancellationToken.None).ConfigureAwait(false);
            }
        }

        foreach ((string reason, int count) in blocked)
        {
            _logger.LogWarning("[AssSubsetter] Quarantine expiry blocked. Reason: {Reason}; Items: {ItemCount}.", reason, count);
        }

        _logger.LogInformation("[AssSubsetter] Quarantine expiry completed. Inspected: {Inspected}; Eligible: {Eligible}; Purged: {Purged}; Blocked: {Blocked}.", inspected, eligible, purged, blocked.Values.Sum());
        progress?.Report(100);
        return purged;
    }

    /// <summary>Reconciles durable manifests with staging, quarantine, source, and database states after restart.</summary>
    /// <param name="cancellationToken">A token that cancels before the next provable transition.</param>
    /// <returns>The number of manifest items advanced through a provable transition.</returns>
    public async Task<int> ReconcileAsync(CancellationToken cancellationToken)
    {
        int reconciled = 0;
        IReadOnlyList<QuarantineManifest> manifests = await _manifestStore.LoadAllAsync(cancellationToken).ConfigureAwait(false);
        foreach (QuarantineManifest loaded in manifests)
        {
            QuarantineManifest manifest = loaded;
            manifest.ValidateCurrentFormat();
            MaintenanceBatch? batch = await _repository.GetMaintenanceBatchAsync(manifest.BatchId, cancellationToken).ConfigureAwait(false);
            if (batch is null)
            {
                var imported = new MaintenanceBatch
                {
                    Id = manifest.BatchId,
                    OperationRequestId = manifest.OperationRequestId,
                    Kind = FontOperationKind.Quarantine,
                    Status = MaintenanceBatchStatus.Partial,
                    StartedUtc = manifest.CreatedUtc,
                    SummaryJson = "{}"
                };
                batch = BatchWithManifest(imported, manifest, MaintenanceBatchStatus.Partial, null);
                await _repository.ImportMaintenanceBatchAsync(
                    batch,
                    manifest.AnalysisBatchId,
                    manifest.OperationIdempotencyKey,
                    manifest.OperationRequestJson,
                    cancellationToken).ConfigureAwait(false);
            }

            for (int index = 0; index < manifest.Items.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                QuarantineManifestItem item = manifest.Items[index];
                if (item.Status is QuarantineItemStatus.Restored or QuarantineItemStatus.Purged)
                {
                    continue;
                }

                if (!RecoveryPathsSafe(manifest.BatchId, item))
                {
                    continue;
                }

                IAsyncDisposable lease = await _coordinator.AcquireExclusiveAsync(
                    item.Replacements.Select(replacement => replacement.Path)
                        .Append(item.SourcePath)
                        .Append(item.StagingPath)
                        .Append(item.QuarantinePath)
                        .Append(RestoreStagingPath(manifest.BatchId, item)),
                    cancellationToken).ConfigureAwait(false);
                await using var leaseScope = lease.ConfigureAwait(false);
                if (item.Status == QuarantineItemStatus.Restoring)
                {
                    QuarantineManifestItem restored = await ReconcileRestoreItemAsync(
                        manifest.BatchId,
                        item,
                        cancellationToken).ConfigureAwait(false);
                    if (restored != item)
                    {
                        manifest = ReplaceItem(manifest, index, restored);
                        await PersistAsync(batch, manifest, CancellationToken.None).ConfigureAwait(false);
                        reconciled++;
                    }

                    continue;
                }

                if (item.Status == QuarantineItemStatus.Cancelled && _fileSystem.FileExists(item.StagingPath))
                {
                    QuarantineManifestItem blocked = item with
                    {
                        Error = "Recovery was blocked because existing staging ownership for the cancelled item cannot be verified."
                    };
                    if (blocked != item)
                    {
                        manifest = ReplaceItem(manifest, index, blocked);
                        await PersistAsync(batch, manifest, CancellationToken.None).ConfigureAwait(false);
                        reconciled++;
                    }

                    continue;
                }

                if (_fileSystem.FileExists(item.SourcePath)
                    && !await SourceSafetyMatchesAsync(manifest.BatchId, item, cancellationToken).ConfigureAwait(false))
                {
                    item = item with { Status = QuarantineItemStatus.EvidenceDrift, Error = "Current source identity, links, bytes, or policy no longer authorize recovery mutation." };
                    manifest = ReplaceItem(manifest, index, item);
                    await PersistAsync(batch, manifest, CancellationToken.None).ConfigureAwait(false);
                    continue;
                }

                bool sourceValid = await MatchesAsync(
                    item.SourcePath,
                    item.Size,
                    item.Sha256,
                    item.ModifiedUtc,
                    cancellationToken).ConfigureAwait(false);
                bool stagingValid = await MatchesAsync(
                    item.StagingPath,
                    item.Size,
                    item.Sha256,
                    null,
                    cancellationToken).ConfigureAwait(false);
                bool quarantineValid = await MatchesAsync(
                    item.QuarantinePath,
                    item.Size,
                    item.Sha256,
                    null,
                    cancellationToken).ConfigureAwait(false);
                if (_fileSystem.FileExists(item.StagingPath) && !stagingValid)
                {
                    QuarantineManifestItem blocked = item with
                    {
                        Status = QuarantineItemStatus.FailedVerification,
                        Error = "Unverified staging data was preserved; inspect the existing copy before retrying recovery."
                    };
                    if (blocked != item)
                    {
                        manifest = ReplaceItem(manifest, index, blocked);
                        await PersistAsync(batch, manifest, CancellationToken.None).ConfigureAwait(false);
                        reconciled++;
                    }

                    continue;
                }

                bool replacementsValid = FaceProofsValid(item);
                foreach (QuarantineReplacementEvidence replacement in item.Replacements)
                {
                    if (!await MatchesAsync(replacement.Path, replacement.Size, replacement.Sha256, null, cancellationToken).ConfigureAwait(false))
                    {
                        replacementsValid = false;
                        break;
                    }
                }

                QuarantineManifestItem? result = null;
                if (replacementsValid && quarantineValid)
                {
                    if (item.Status == QuarantineItemStatus.Quarantined)
                    {
                        result = item;
                    }
                    else
                    {
                        if (item.Status != QuarantineItemStatus.Promoted)
                        {
                            item = item with { Status = QuarantineItemStatus.Promoted, Error = null };
                            manifest = ReplaceItem(manifest, index, item);
                            await PersistAsync(batch, manifest, CancellationToken.None).ConfigureAwait(false);
                        }

                        bool sourceStillValid = await MatchesAsync(
                            item.SourcePath,
                            item.Size,
                            item.Sha256,
                            item.ModifiedUtc,
                            cancellationToken).ConfigureAwait(false);
                        bool replacementsStillValid = await ReplacementsMatchAsync(item, cancellationToken).ConfigureAwait(false);
                        if (!replacementsStillValid)
                        {
                            result = item with
                            {
                                Status = QuarantineItemStatus.ReplacementMissing,
                                Error = "A retained replacement changed after final quarantine promotion."
                            };
                        }
                        else
                        {
                            if (sourceStillValid)
                            {
                                if (!await SourceSafetyMatchesAsync(manifest.BatchId, item, cancellationToken).ConfigureAwait(false))
                                {
                                    item = item with { Status = QuarantineItemStatus.EvidenceDrift, Error = "Current source safety changed before recovered source deletion." };
                                    manifest = ReplaceItem(manifest, index, item);
                                    await PersistAsync(batch, manifest, CancellationToken.None).ConfigureAwait(false);
                                    continue;
                                }

                                _coordinator.SetPathAvailable(item.SourcePath, false);
                                try
                                {
                                    await _fileSystem.DeleteAsync(item.SourcePath, cancellationToken).ConfigureAwait(false);
                                }
                                catch (UnauthorizedAccessException exception)
                                {
                                    result = await StabilizeIoFailureAsync(item, exception, true).ConfigureAwait(false);
                                }
                            }

                            result ??= Quarantined(item);
                        }
                    }

                    if (_fileSystem.FileExists(item.StagingPath))
                    {
                        if (!RecoveryPathsSafe(manifest.BatchId, item))
                        {
                            continue;
                        }

                        await _fileSystem.DeleteAsync(item.StagingPath, cancellationToken).ConfigureAwait(false);
                    }
                }
                else if (replacementsValid && stagingValid && sourceValid && !_fileSystem.FileExists(item.QuarantinePath))
                {
                    if (!RecoveryPathsSafe(manifest.BatchId, item))
                    {
                        continue;
                    }

                    await _fileSystem.MoveAsync(item.StagingPath, item.QuarantinePath, cancellationToken).ConfigureAwait(false);
                    if (!await MatchesAsync(item.QuarantinePath, item.Size, item.Sha256, null, cancellationToken).ConfigureAwait(false))
                    {
                        result = item with
                        {
                            Status = QuarantineItemStatus.FailedVerification,
                            Error = "The reconciled final quarantine copy failed verification."
                        };
                    }
                    else
                    {
                        item = item with { Status = QuarantineItemStatus.Promoted, Error = null };
                        manifest = ReplaceItem(manifest, index, item);
                        await PersistAsync(batch, manifest, CancellationToken.None).ConfigureAwait(false);
                        bool sourceStillValid = await MatchesAsync(
                            item.SourcePath,
                            item.Size,
                            item.Sha256,
                            item.ModifiedUtc,
                            cancellationToken).ConfigureAwait(false);
                        bool replacementsStillValid = await ReplacementsMatchAsync(item, cancellationToken).ConfigureAwait(false);
                        if (!sourceStillValid)
                        {
                            result = item with
                            {
                                Status = QuarantineItemStatus.EvidenceDrift,
                                Error = "Source evidence changed while reconciling a promoted quarantine copy."
                            };
                        }
                        else if (!replacementsStillValid)
                        {
                            result = item with
                            {
                                Status = QuarantineItemStatus.ReplacementMissing,
                                Error = "A retained replacement changed while reconciling a promoted quarantine copy."
                            };
                        }
                        else
                        {
                            if (!await SourceSafetyMatchesAsync(manifest.BatchId, item, cancellationToken).ConfigureAwait(false))
                            {
                                item = item with { Status = QuarantineItemStatus.EvidenceDrift, Error = "Current source safety changed after recovered staging promotion." };
                                manifest = ReplaceItem(manifest, index, item);
                                await PersistAsync(batch, manifest, CancellationToken.None).ConfigureAwait(false);
                                continue;
                            }

                            _coordinator.SetPathAvailable(item.SourcePath, false);
                            try
                            {
                                await _fileSystem.DeleteAsync(item.SourcePath, cancellationToken).ConfigureAwait(false);
                                result = Quarantined(item);
                            }
                            catch (UnauthorizedAccessException exception)
                            {
                                result = await StabilizeIoFailureAsync(item, exception, true).ConfigureAwait(false);
                            }
                        }
                    }
                }

                if (result is not null && result != item)
                {
                    manifest = ReplaceItem(manifest, index, result);
                    await PersistAsync(batch, manifest, CancellationToken.None).ConfigureAwait(false);
                    reconciled++;
                }
            }

            if (batch.Status == MaintenanceBatchStatus.Running
                && manifest.Items.Any(item => item.Status == QuarantineItemStatus.FailedIo))
            {
                batch = BatchWithManifest(batch, manifest, MaintenanceBatchStatus.Partial, _timeProvider.GetUtcNow());
                await PersistAsync(batch, manifest, CancellationToken.None).ConfigureAwait(false);
            }
        }

        return reconciled;
    }

    private bool PurgePathsSafe(long batchId, QuarantineManifestItem item)
        => RecoveryPathsSafe(batchId, item)
            && (_policy().GetPolicy(item.QuarantinePath) & FontDirectoryPolicy.Protected) == 0;

    private bool RecoveryPathsSafe(long batchId, QuarantineManifestItem item)
    {
        FontDirectoryPolicy current = _policy().GetPolicy(item.SourcePath);
        if ((current & FontDirectoryPolicy.Custom) == 0 || (current & FontDirectoryPolicy.Protected) != 0)
        {
            return false;
        }

        return item.Replacements.Select(replacement => replacement.Path)
            .Append(item.SourcePath)
            .Append(item.StagingPath)
            .Append(item.QuarantinePath)
            .Append(RestoreStagingPath(batchId, item))
            .All(_fileSystem.IsPhysicalPath);
    }

    private QuarantineManifest BuildManifest(
        MaintenanceBatch batch,
        AnalysisBatch analysis,
        OperationRequest request,
        IReadOnlyList<long> selectedFileIds,
        Dictionary<long, FontFileRecord> files)
    {
        Dictionary<(long Left, long Right), FontRelationRecord> relations = analysis.Relations
            .ToDictionary(relation => (relation.LeftFaceId, relation.RightFaceId));
        QuarantineManifestItem[] items = selectedFileIds.Distinct().Order().Select(fileId =>
        {
            if (!files.TryGetValue(fileId, out FontFileRecord? file))
            {
                throw new InvalidOperationException($"Selected physical Font File {fileId} is not in the published inventory.");
            }

            if (file.DirectoryPolicy.HasFlag(FontDirectoryPolicy.Protected)
                || file.LinkKind != FontLinkKind.None
                || file.Health != FontFileHealth.Healthy)
            {
                throw new InvalidOperationException($"Selected physical Font File {fileId} is protected or unsafe.");
            }

            DuplicateGroupRecord[] groups = analysis.Groups
                .Where(group => group.Members.Any(member => member.FileId == fileId
                    && member.Decision == DuplicateGroupMemberDecision.QuarantineCandidate))
                .ToArray();
            if (groups.Length == 0)
            {
                throw new InvalidOperationException($"Selected physical Font File {fileId} is not a verified quarantine candidate.");
            }

            FontFileRecord[] retainedFiles = groups
                .SelectMany(group => group.Members)
                .Where(member => member.Decision == DuplicateGroupMemberDecision.Retained)
                .Select(member => files[member.FileId])
                .DistinctBy(replacement => replacement.Id)
                .ToArray();
            QuarantineFaceReplacementProof[] faceProofs = file.Faces
                .Select(face => CreateFaceReplacementProof(face, retainedFiles, relations))
                .ToArray();
            QuarantineReplacementEvidence[] replacements = faceProofs
                .Select(proof => files[proof.ReplacementFileId])
                .DistinctBy(replacement => replacement.Id)
                .Select(replacement => new QuarantineReplacementEvidence(
                    replacement.Id,
                    replacement.Path,
                    replacement.Size,
                    replacement.Sha256))
                .ToArray();
            if (replacements.Length == 0)
            {
                throw new InvalidOperationException($"Selected physical Font File {fileId} has no retained replacement.");
            }

            string itemDirectory = Path.Combine(BatchDirectory(batch.Id), "items");
            string stem = fileId.ToString("D20", CultureInfo.InvariantCulture);
            return new QuarantineManifestItem
            {
                FileId = fileId,
                SourcePath = file.Path,
                SourcePhysicalIdentity = file.PhysicalIdentity,
                StagingPath = Path.Combine(itemDirectory, stem + ".stage"),
                QuarantinePath = Path.Combine(itemDirectory, stem + ".font"),
                Size = file.Size,
                ModifiedUtc = file.ModifiedUtc,
                Sha256 = file.Sha256,
                Replacements = replacements,
                FaceReplacementProofs = faceProofs,
                Status = QuarantineItemStatus.Pending
            };
        }).ToArray();
        return new QuarantineManifest
        {
            Version = 2,
            EvidenceVersion = analysis.EvidenceVersion,
            BatchId = batch.Id,
            OperationRequestId = batch.OperationRequestId,
            OperationIdempotencyKey = request.IdempotencyKey,
            OperationRequestJson = request.RequestJson,
            AnalysisBatchId = analysis.Id,
            CreatedUtc = batch.StartedUtc,
            Items = items
        };
    }

    private static QuarantineFaceReplacementProof CreateFaceReplacementProof(
        FontFaceRecord target,
        IReadOnlyList<FontFileRecord> retainedFiles,
        Dictionary<(long Left, long Right), FontRelationRecord> relations)
    {
        foreach (FontFileRecord retainedFile in retainedFiles)
        {
            foreach (FontFaceRecord replacement in retainedFile.Faces)
            {
                if (!string.Equals(replacement.Technology, target.Technology, StringComparison.Ordinal))
                {
                    continue;
                }

                long left = Math.Min(replacement.Id, target.Id);
                long right = Math.Max(replacement.Id, target.Id);
                if (!relations.TryGetValue((left, right), out FontRelationRecord? relation)
                    || !AutomaticRelationshipKinds.Contains(relation.RelationshipKind)
                    || (relation.ReplacementDirection != FontReplacementDirection.Both
                        && !(replacement.Id == left && relation.ReplacementDirection == FontReplacementDirection.LeftReplacesRight)
                        && !(replacement.Id == right && relation.ReplacementDirection == FontReplacementDirection.RightReplacesLeft)))
                {
                    continue;
                }

                return new QuarantineFaceReplacementProof(
                    target.Id,
                    replacement.Id,
                    retainedFile.Id,
                    relation.RelationshipKind,
                    relation.ReplacementDirection,
                    target.Technology);
            }
        }

        throw new InvalidOperationException($"Face {target.Id} has no persisted direct retained replacement.");
    }

    private async Task EnsureSpaceAsync(
        Dictionary<long, FontFileRecord> files,
        IReadOnlyList<long> selectedFileIds,
        CancellationToken cancellationToken)
    {
        long required = 0;
        foreach (long fileId in selectedFileIds)
        {
            if (!files.TryGetValue(fileId, out FontFileRecord? file))
            {
                throw new InvalidOperationException($"Selected Font File {fileId} is absent from the published inventory.");
            }

            if (!_fileSystem.IsSameVolume(file.Path, _quarantineRoot))
            {
                required = checked(required + file.Size);
            }
        }

        if (required == 0)
        {
            return;
        }

        long available = await _fileSystem.GetAvailableSpaceAsync(_quarantineRoot, cancellationToken).ConfigureAwait(false);
        if (available < required)
        {
            throw new IOException($"Quarantine requires {required} bytes but only {available} bytes are available.");
        }
    }

    private async Task<QuarantineManifestItem> QuarantineItemAsync(
        long batchId,
        QuarantineManifestItem item,
        Func<QuarantineManifestItem, Task> persistIntermediateAsync,
        CancellationToken cancellationToken)
    {
        bool quarantineDestinationExisted = _fileSystem.FileExists(item.QuarantinePath);
        bool sourceMarkedUnavailable = false;
        IAsyncDisposable? lease = null;
        try
        {
            lease = await _coordinator.AcquireExclusiveAsync(
                item.Replacements.Select(replacement => replacement.Path)
                    .Append(item.SourcePath).Append(item.StagingPath).Append(item.QuarantinePath),
                cancellationToken).ConfigureAwait(false);
            if (!await SourceSafetyMatchesAsync(batchId, item, cancellationToken).ConfigureAwait(false))
            {
                return item with { Status = QuarantineItemStatus.EvidenceDrift, Error = "Current directory or physical path safety no longer authorizes quarantine." };
            }

            if (!await MatchesAsync(item.SourcePath, item.Size, item.Sha256, item.ModifiedUtc, cancellationToken).ConfigureAwait(false))
            {
                return item with { Status = QuarantineItemStatus.EvidenceDrift, Error = "Source evidence changed after analysis." };
            }

            if (!FaceProofsValid(item))
            {
                return item with { Status = QuarantineItemStatus.FailedVerification, Error = "The manifest does not prove a direct replacement for every source Face." };
            }

            foreach (QuarantineReplacementEvidence replacement in item.Replacements)
            {
                if (!await MatchesAsync(replacement.Path, replacement.Size, replacement.Sha256, null, cancellationToken).ConfigureAwait(false))
                {
                    return item with { Status = QuarantineItemStatus.ReplacementMissing, Error = "A retained replacement is missing or changed." };
                }
            }

            if (!await ReplacementsMatchAsync(item, cancellationToken).ConfigureAwait(false))
            {
                return item with { Status = QuarantineItemStatus.ReplacementMissing, Error = "A retained replacement changed before quarantine mutation." };
            }

            if (_fileSystem.IsSameVolume(item.SourcePath, item.QuarantinePath))
            {
                if (!await SourceSafetyMatchesAsync(batchId, item, cancellationToken).ConfigureAwait(false))
                {
                    return item with { Status = QuarantineItemStatus.EvidenceDrift, Error = "Current path safety changed before quarantine movement." };
                }

                if (quarantineDestinationExisted)
                {
                    return item with { Status = QuarantineItemStatus.DestinationOccupied, Error = "The generated quarantine destination is already occupied." };
                }

                _coordinator.SetPathAvailable(item.SourcePath, false);
                sourceMarkedUnavailable = true;
                await _fileSystem.MoveAsync(item.SourcePath, item.QuarantinePath, cancellationToken).ConfigureAwait(false);
                return Quarantined(item);
            }

            if (!await SourceSafetyMatchesAsync(batchId, item, cancellationToken).ConfigureAwait(false))
            {
                return item with { Status = QuarantineItemStatus.EvidenceDrift, Error = "Current path safety changed before quarantine staging." };
            }

            await _fileSystem.CopyAsync(item.SourcePath, item.StagingPath, cancellationToken).ConfigureAwait(false);
            if (!await MatchesAsync(item.StagingPath, item.Size, item.Sha256, null, cancellationToken).ConfigureAwait(false))
            {
                await _fileSystem.DeleteAsync(item.StagingPath, CancellationToken.None).ConfigureAwait(false);
                return item with { Status = QuarantineItemStatus.FailedVerification, Error = "The staging copy failed size or SHA-256 verification." };
            }

            item = item with { Status = QuarantineItemStatus.Staged, Error = null };
            await persistIntermediateAsync(item).ConfigureAwait(false);
            if (!await SourceSafetyMatchesAsync(batchId, item, cancellationToken).ConfigureAwait(false))
            {
                return item with { Status = QuarantineItemStatus.EvidenceDrift, Error = "Current path safety changed before quarantine promotion." };
            }

            await _fileSystem.MoveAsync(item.StagingPath, item.QuarantinePath, cancellationToken).ConfigureAwait(false);
            if (!await MatchesAsync(item.QuarantinePath, item.Size, item.Sha256, null, cancellationToken).ConfigureAwait(false))
            {
                return item with { Status = QuarantineItemStatus.FailedVerification, Error = "The promoted quarantine copy failed size or SHA-256 verification." };
            }

            item = item with { Status = QuarantineItemStatus.Promoted, Error = null };
            await persistIntermediateAsync(item).ConfigureAwait(false);
            if (!await MatchesAsync(item.SourcePath, item.Size, item.Sha256, item.ModifiedUtc, cancellationToken).ConfigureAwait(false))
            {
                return item with { Status = QuarantineItemStatus.EvidenceDrift, Error = "Source evidence changed during cross-volume quarantine." };
            }

            if (!await ReplacementsMatchAsync(item, cancellationToken).ConfigureAwait(false))
            {
                return item with { Status = QuarantineItemStatus.ReplacementMissing, Error = "A retained replacement changed during cross-volume quarantine." };
            }

            if (!await SourceSafetyMatchesAsync(batchId, item, cancellationToken).ConfigureAwait(false))
            {
                return item with { Status = QuarantineItemStatus.EvidenceDrift, Error = "Current path safety changed before source deletion." };
            }

            _coordinator.SetPathAvailable(item.SourcePath, false);
            sourceMarkedUnavailable = true;
            await _fileSystem.DeleteAsync(item.SourcePath, cancellationToken).ConfigureAwait(false);
            return Quarantined(item);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (sourceMarkedUnavailable
                && await SourceSafetyMatchesAsync(batchId, item, CancellationToken.None).ConfigureAwait(false))
            {
                _coordinator.SetPathAvailable(item.SourcePath, true);
            }

            return item with { Status = QuarantineItemStatus.Cancelled, Error = "Cancellation preserved existing copies for reconciliation." };
        }
        catch (UnauthorizedAccessException exception)
        {
            return await StabilizeIoFailureAsync(item, exception, !quarantineDestinationExisted).ConfigureAwait(false);
        }
        finally
        {
            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<QuarantineManifestItem> StabilizeIoFailureAsync(
        QuarantineManifestItem item,
        Exception exception,
        bool mayRemoveFinalCopy)
    {
        bool sourceValid = await MatchesAsync(item.SourcePath, item.Size, item.Sha256, item.ModifiedUtc, CancellationToken.None).ConfigureAwait(false);
        if (!sourceValid && !_fileSystem.FileExists(item.SourcePath)
            && await MatchesAsync(item.QuarantinePath, item.Size, item.Sha256, null, CancellationToken.None).ConfigureAwait(false))
        {
            return Quarantined(item);
        }

        var cleanupErrors = new List<string>();
        if (sourceValid)
        {
            if (mayRemoveFinalCopy
                && await MatchesAsync(item.QuarantinePath, item.Size, item.Sha256, null, CancellationToken.None).ConfigureAwait(false))
            {
                try
                {
                    await _fileSystem.DeleteAsync(item.QuarantinePath, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
                {
                    cleanupErrors.Add("The generated quarantine copy could not be removed: " + cleanupException.Message);
                }
            }

            if (_fileSystem.FileExists(item.StagingPath))
            {
                try
                {
                    await _fileSystem.DeleteAsync(item.StagingPath, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
                {
                    cleanupErrors.Add("The staging copy could not be removed: " + cleanupException.Message);
                }
            }

            _coordinator.SetPathAvailable(item.SourcePath, true);
        }

        string error = exception is UnauthorizedAccessException
            ? "permission-denied: Jellyfin cannot move or remove the source font; check write access to its parent directory."
            : "io-failure: " + exception.Message;
        if (cleanupErrors.Count > 0)
        {
            error += " " + string.Join(" ", cleanupErrors);
        }

        return item with { Status = QuarantineItemStatus.FailedIo, Error = error };
    }

    private async Task<bool> SourceSafetyMatchesAsync(long batchId, QuarantineManifestItem item, CancellationToken cancellationToken)
    {
        if (!RecoveryPathsSafe(batchId, item))
        {
            return false;
        }

        try
        {
            QuarantineFileEvidence? current = await _fileSystem.ReadEvidenceAsync(item.SourcePath, cancellationToken).ConfigureAwait(false);
            return current is not null
                && current.LinkCount == 1
                && !string.IsNullOrEmpty(item.SourcePhysicalIdentity)
                && string.Equals(current.PhysicalIdentity, item.SourcePhysicalIdentity, StringComparison.Ordinal)
                && current.Size == item.Size
                && current.ModifiedUtc == item.ModifiedUtc
                && current.Sha256.Span.SequenceEqual(item.Sha256.Span)
                && RecoveryPathsSafe(batchId, item);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<bool> RestoreFileAsync(
        long batchId,
        QuarantineManifestItem item,
        CancellationToken cancellationToken)
    {
        if (!RecoveryPathsSafe(batchId, item))
        {
            return false;
        }

        if (_fileSystem.IsSameVolume(item.QuarantinePath, item.SourcePath))
        {
            await _fileSystem.MoveForRestoreAsync(item.QuarantinePath, item.SourcePath, cancellationToken).ConfigureAwait(false);
            return true;
        }

        string stagingPath = RestoreStagingPath(batchId, item);
        await _fileSystem.CopyAsync(item.QuarantinePath, stagingPath, cancellationToken).ConfigureAwait(false);
        if (!await MatchesAsync(stagingPath, item.Size, item.Sha256, null, cancellationToken).ConfigureAwait(false))
        {
            await _fileSystem.DeleteAsync(stagingPath, CancellationToken.None).ConfigureAwait(false);
            throw new IOException("The restore staging copy failed verification.");
        }

        if (!RecoveryPathsSafe(batchId, item))
        {
            return false;
        }

        await _fileSystem.MoveForRestoreAsync(stagingPath, item.SourcePath, cancellationToken).ConfigureAwait(false);
        if (!RecoveryPathsSafe(batchId, item))
        {
            return false;
        }

        await _fileSystem.DeleteAsync(item.QuarantinePath, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<QuarantineManifestItem> ReconcileRestoreItemAsync(
        long batchId,
        QuarantineManifestItem item,
        CancellationToken cancellationToken)
    {
        string stagingPath = RestoreStagingPath(batchId, item);
        bool sourceExists = _fileSystem.FileExists(item.SourcePath);
        bool sourceValid = await MatchesAsync(item.SourcePath, item.Size, item.Sha256, null, cancellationToken).ConfigureAwait(false);
        bool quarantineExists = _fileSystem.FileExists(item.QuarantinePath);
        bool quarantineValid = await MatchesAsync(item.QuarantinePath, item.Size, item.Sha256, null, cancellationToken).ConfigureAwait(false);
        bool stagingExists = _fileSystem.FileExists(stagingPath);
        bool stagingValid = await MatchesAsync(stagingPath, item.Size, item.Sha256, null, cancellationToken).ConfigureAwait(false);
        if (sourceExists && !sourceValid)
        {
            return item with { Status = QuarantineItemStatus.DestinationOccupied, Error = "The original path became occupied during restore." };
        }

        if (sourceValid)
        {
            if (quarantineExists && !quarantineValid)
            {
                return item with { Status = QuarantineItemStatus.FailedVerification, Error = "Restore completed, but the remaining quarantine path failed verification." };
            }

            if (quarantineValid)
            {
                if (!RecoveryPathsSafe(batchId, item))
                {
                    return item;
                }

                await _fileSystem.DeleteAsync(item.QuarantinePath, cancellationToken).ConfigureAwait(false);
            }

            if (stagingExists)
            {
                if (!stagingValid)
                {
                    return item with { Status = QuarantineItemStatus.FailedVerification, Error = "Restore completed, but its staging path contains unverified data." };
                }

                if (!RecoveryPathsSafe(batchId, item))
                {
                    return item;
                }

                await _fileSystem.DeleteAsync(stagingPath, cancellationToken).ConfigureAwait(false);
            }

            return Restored(item);
        }

        if (stagingValid)
        {
            if (!RecoveryPathsSafe(batchId, item))
            {
                return item;
            }

            await _fileSystem.MoveForRestoreAsync(stagingPath, item.SourcePath, cancellationToken).ConfigureAwait(false);
            if (quarantineExists && !quarantineValid)
            {
                return item with { Status = QuarantineItemStatus.FailedVerification, Error = "The source was restored, but the quarantine path contains unverified data." };
            }

            if (quarantineValid)
            {
                if (!RecoveryPathsSafe(batchId, item))
                {
                    return item;
                }

                await _fileSystem.DeleteAsync(item.QuarantinePath, cancellationToken).ConfigureAwait(false);
            }

            return Restored(item);
        }

        if (quarantineValid)
        {
            if (stagingExists)
            {
                if (!RecoveryPathsSafe(batchId, item))
                {
                    return item;
                }

                await _fileSystem.DeleteAsync(stagingPath, cancellationToken).ConfigureAwait(false);
            }

            return item with { Status = QuarantineItemStatus.Quarantined, Error = "Interrupted restore staging was rolled back." };
        }

        return item with { Status = QuarantineItemStatus.FailedVerification, Error = "No verified restore source remains." };
    }

    private async Task<bool> ReplacementsMatchAsync(
        QuarantineManifestItem item,
        CancellationToken cancellationToken)
    {
        foreach (QuarantineReplacementEvidence replacement in item.Replacements)
        {
            if (!await MatchesAsync(replacement.Path, replacement.Size, replacement.Sha256, null, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    private static bool FaceProofsValid(QuarantineManifestItem item)
    {
        if (item.FaceReplacementProofs.Count == 0
            || item.FaceReplacementProofs.Select(proof => proof.TargetFaceId).Distinct().Count() != item.FaceReplacementProofs.Count)
        {
            return false;
        }

        HashSet<long> replacementFileIds = item.Replacements.Select(replacement => replacement.FileId).ToHashSet();
        return item.FaceReplacementProofs.All(proof =>
        {
            long left = Math.Min(proof.ReplacementFaceId, proof.TargetFaceId);
            long right = Math.Max(proof.ReplacementFaceId, proof.TargetFaceId);
            bool directionValid = proof.ReplacementDirection == FontReplacementDirection.Both
                || (proof.ReplacementFaceId == left && proof.ReplacementDirection == FontReplacementDirection.LeftReplacesRight)
                || (proof.ReplacementFaceId == right && proof.ReplacementDirection == FontReplacementDirection.RightReplacesLeft);
            return replacementFileIds.Contains(proof.ReplacementFileId)
                && AutomaticRelationshipKinds.Contains(proof.RelationshipKind)
                && directionValid
                && !string.IsNullOrWhiteSpace(proof.Technology);
        });
    }

    private QuarantineManifestItem Restored(QuarantineManifestItem item)
    {
        _coordinator.SetPathAvailable(item.SourcePath, true);
        return item with
        {
            Status = QuarantineItemStatus.Restored,
            RestoredUtc = _timeProvider.GetUtcNow(),
            Error = null
        };
    }

    private static string RestoreStagingPath(long batchId, QuarantineManifestItem item)
    {
        string sourceDirectory = Path.GetDirectoryName(item.SourcePath)
            ?? throw new InvalidOperationException("The original path has no parent directory.");
        return Path.Combine(
            sourceDirectory,
            ".ass-subsetter-restore-"
                + batchId.ToString("D20", CultureInfo.InvariantCulture)
                + "-"
                + item.FileId.ToString("D20", CultureInfo.InvariantCulture));
    }

    private async Task<bool> MatchesAsync(
        string path,
        long size,
        ReadOnlyMemory<byte> sha256,
        DateTimeOffset? modifiedUtc,
        CancellationToken cancellationToken)
    {
        QuarantineFileEvidence? current = await _fileSystem.ReadEvidenceAsync(path, cancellationToken).ConfigureAwait(false);
        return current is not null
            && current.Size == size
            && (!modifiedUtc.HasValue || current.ModifiedUtc == modifiedUtc.Value)
            && current.Sha256.Span.SequenceEqual(sha256.Span);
    }

    private QuarantineManifestItem Quarantined(QuarantineManifestItem item)
    {
        DateTimeOffset quarantinedUtc = item.QuarantinedUtc ?? _timeProvider.GetUtcNow();
        return item with
        {
            Status = QuarantineItemStatus.Quarantined,
            QuarantinedUtc = quarantinedUtc,
            ExpiresUtc = item.ExpiresUtc ?? quarantinedUtc.AddDays(30),
            Error = null
        };
    }

    private async Task PersistAsync(
        MaintenanceBatch batch,
        QuarantineManifest manifest,
        CancellationToken cancellationToken)
    {
        await _manifestStore.SaveAsync(manifest, cancellationToken).ConfigureAwait(false);
        MaintenanceBatch next = BatchWithManifest(batch, manifest, batch.Status, batch.CompletedUtc);
        await _repository.UpdateMaintenanceBatchAsync(next, cancellationToken).ConfigureAwait(false);
    }

    private static MaintenanceBatch BatchWithManifest(
        MaintenanceBatch batch,
        QuarantineManifest manifest,
        MaintenanceBatchStatus status,
        DateTimeOffset? completedUtc)
    {
        QuarantineItemRecord[] items = manifest.Items.Select(item => new QuarantineItemRecord
        {
            MaintenanceBatchId = batch.Id,
            FileId = item.FileId,
            SourcePath = item.SourcePath,
            StagingPath = item.StagingPath,
            QuarantinePath = item.QuarantinePath,
            Sha256 = item.Sha256,
            Size = item.Size,
            Status = item.Status,
            QuarantinedUtc = item.QuarantinedUtc,
            ExpiresUtc = item.ExpiresUtc,
            RestoredUtc = item.RestoredUtc,
            Error = item.Error
        }).ToArray();
        string summary = JsonSerializer.Serialize(
            new
            {
                items = items.Length,
                quarantined = items.Count(item => item.Status == QuarantineItemStatus.Quarantined),
                failed = items.Count(item => item.Status is not QuarantineItemStatus.Quarantined)
            },
            JsonOptions);
        return batch with { Status = status, CompletedUtc = completedUtc, SummaryJson = summary, Items = items };
    }

    private static QuarantineManifest ReplaceItem(QuarantineManifest manifest, int index, QuarantineManifestItem item)
    {
        QuarantineManifestItem[] items = manifest.Items.ToArray();
        items[index] = item;
        return manifest with { Items = items };
    }

    private static FontOperationRequestPayload ReadPayload(OperationRequest request, FontOperationKind expectedKind)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Id <= 0 || request.Kind != expectedKind)
        {
            throw new ArgumentException($"A persisted {expectedKind} request is required.", nameof(request));
        }

        return JsonSerializer.Deserialize<FontOperationRequestPayload>(request.RequestJson, JsonOptions)
            ?? throw new InvalidOperationException("The operation request payload is invalid.");
    }

    private string BatchDirectory(long batchId)
        => Path.Combine(_quarantineRoot, batchId.ToString("D20", CultureInfo.InvariantCulture));
}
