using System.Text.Json;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;
using Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Persistence;

internal sealed class InMemoryFontRepository : IFontRepository
{
    private readonly object _sync = new();
    private readonly Dictionary<long, Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot> _inputs = [];
    private readonly Dictionary<long, IReadOnlyList<FontFileRecord>> _generations = [];
    private readonly HashSet<long> _buildingGenerations = [];
    private readonly Dictionary<long, DateTimeOffset> _generationCreatedUtc = [];
    private readonly List<AnalysisBatch> _analysisBatches = [];
    private readonly List<OperationRequest> _operationRequests = [];
    private readonly List<MaintenanceBatch> _maintenanceBatches = [];
    private readonly List<PerformanceSample> _performanceSamples = [];
    private readonly Dictionary<long, (string EvidenceKey, string Payload)> _deepEvidence = [];
    private long _nextGenerationId;
    private long _nextAnalysisBatchId;
    private long _nextOperationRequestId;
    private long _nextMaintenanceBatchId;
    private long? _publishedGenerationId;
    private FontRenameCandidateState? _renameCandidateState;
    private IReadOnlyList<FontRenameCandidate> _renameCandidates = [];

    public Action<int>? BeforePrune { get; set; }

    public bool ThrowOnFullRenameReads { get; set; }

    public Task<FontRenameStatePage> LoadRenameStatePageAsync(string status, int page, int pageSize, FontRenameCandidateReadRequirements requirements, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SqliteFontRepository.ValidateRenameStateFilter(status);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        lock (_sync)
        {
            var context = RenameContext(requirements);
            var outcomes = _maintenanceBatches.SelectMany(batch => batch.RenameItems.Select(item => item with { MaintenanceBatchId = batch.Id, OperationRequestId = batch.OperationRequestId })).ToList();
            var requests = _operationRequests.Where(request => request.Kind == FontOperationKind.NormalizeNames)
                .Select(request => (Request: request, Payload: FontOperationRequestPayloadCodec.ParseRenameExecution(request.RequestJson))).ToArray();
            var reservedIdentities = requests.Where(entry => entry.Request.Status is OperationRequestStatus.Pending or OperationRequestStatus.Running)
                .SelectMany(entry => entry.Payload.Items).Select(item => item.Evidence.PhysicalIdentity).Where(identity => !string.IsNullOrEmpty(identity)).ToHashSet(StringComparer.Ordinal);
            var recorded = outcomes.Select(item => (item.OperationRequestId, item.FileId)).ToHashSet();
            var reserved = requests.Where(entry => entry.Request.Status is OperationRequestStatus.Pending or OperationRequestStatus.Running)
                .SelectMany(entry => entry.Payload.Items).SelectMany(item => new[] { item.OldPath, item.NewPath }).ToHashSet(FontPathComparison.Comparer);
            foreach (var entry in requests)
            {
                outcomes.AddRange(entry.Payload.Items.Where(item => !recorded.Contains((entry.Request.Id, item.FileId))).Select(item => item with
                {
                    OperationRequestId = entry.Request.Id,
                    Status = entry.Request.Status == OperationRequestStatus.Failed ? RenameItemStatus.FailedIo
                        : entry.Request.Status is OperationRequestStatus.Cancelled or OperationRequestStatus.Complete ? RenameItemStatus.Skipped : RenameItemStatus.Pending,
                    Error = entry.Request.Error ?? (entry.Request.Status == OperationRequestStatus.Cancelled ? "request-cancelled" : null)
                }));
            }

            static string Key(RenameItemRecord item) => FontRenameStateIdentity.SourceKey(item.OldPath, item.NewPath, item.Evidence);
            var latest = outcomes.OrderByDescending(item => item.OperationRequestId).ThenByDescending(item => item.MaintenanceBatchId)
                .GroupBy(Key).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
            var rows = new List<FontRenameStateRow>();
            foreach (var item in status is "renamed" or "rolledback" ? outcomes : latest.Values.AsEnumerable())
            {
                if (FontRenameStateIdentity.Category(item.Status, item.Error) != status) continue;
                string key = Key(item);
                bool rollback = item.Status == RenameItemStatus.Renamed && item.MaintenanceBatchId.HasValue
                    && !(latest[key].OperationRequestId > item.OperationRequestId && latest[key].Status is RenameItemStatus.RolledBack or RenameItemStatus.RollingBack);
                rows.Add(new("execution:" + item.OperationRequestId + ":" + item.MaintenanceBatchId + ":" + key, item.MaintenanceBatchId, item.OperationRequestId, item.Evidence.GenerationId, item.FileId, false, rollback, item));
            }

            if (context.IsCurrent && status is "pending" or "skipped")
            {
                foreach (var candidate in _renameCandidates)
                {
                    var item = FontRenameCandidateService.ToItem(candidate);
                    string key = Key(item);
                    if (FontRenameStateIdentity.Category(item.Status, item.Error) != status
                        || (latest.TryGetValue(key, out var saved) && (FontRenameStateIdentity.Category(saved.Status, saved.Error) == "pending" || FontRenameStateIdentity.Category(saved.Status, saved.Error) == status))
                        || reserved.Contains(item.OldPath) || reserved.Contains(item.NewPath) || reservedIdentities.Contains(item.Evidence.PhysicalIdentity)) continue;
                    rows.Add(new("current:" + context.State!.GenerationId + ":" + context.State.RuleFingerprint + ":" + key, null, null, item.Evidence.GenerationId, item.FileId, candidate.SkipReason is null, false, item));
                }
            }

            var ordered = rows.OrderBy(row => row.Item.OldPath, StringComparer.Ordinal).ThenBy(row => row.Key, StringComparer.Ordinal).ToArray();
            return Task.FromResult(new FontRenameStatePage(context, ordered.Skip((int)Math.Min(((long)page - 1) * pageSize, int.MaxValue)).Take(pageSize).ToArray(), ordered.LongLength));
        }
    }

    public Task<FontRenameCandidateReadContext> GetRenameCandidateContextAsync(FontRenameCandidateReadRequirements requirements, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return Task.FromResult(RenameContext(requirements));
        }
    }

    public IReadOnlyList<long>? LastRenameSelection { get; private set; }

    public Task<FontRenameCandidateSelection> LoadRenameCandidatesAsync(IReadOnlyList<long>? fileIds, FontRenameCandidateReadRequirements requirements, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var context = RenameContext(requirements);
            if (ThrowOnFullRenameReads && fileIds is null) throw new InvalidOperationException("GET must not load all candidates");
            LastRenameSelection = fileIds;
            var selected = fileIds?.ToHashSet();
            return Task.FromResult(new FontRenameCandidateSelection(context,
                context.IsCurrent ? _renameCandidates.Where(item => selected is null || selected.Contains(item.File.Id)).OrderBy(item => item.File.Id).ToArray() : []));
        }
    }

    public Task SetRenameCandidateStateAsync(FontRenameCandidateState state, FontRenameCandidateReadRequirements requirements, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            SqliteFontRepository.ValidateRenameCandidateWrite(RenameContext(requirements), state, requirements);
            if (_renameCandidateState?.OperationRequestId is long currentRequest && state.OperationRequestId != currentRequest
                && (state.Status != FontRenameCandidateGenerationStatus.Pending || (state.OperationRequestId is null || state.OperationRequestId < currentRequest)))
            {
                throw new InvalidOperationException("A newer rename candidate request has replaced this calculation.");
            }

            _renameCandidateState = state;
            return Task.CompletedTask;
        }
    }

    public Task SaveRenameCandidatesAsync(FontRenameCandidatePreparation preparation, FontRenameCandidateReadRequirements requirements, long? expectedRequestId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            SqliteFontRepository.ValidateRenameCandidateWrite(RenameContext(requirements), preparation.State, requirements);
            if (_renameCandidateState?.OperationRequestId != expectedRequestId || preparation.State.OperationRequestId != expectedRequestId)
            {
                throw new InvalidOperationException("A newer rename candidate request has replaced this calculation.");
            }

            if (preparation.Items.Any(item => item.File.GenerationId != preparation.State.GenerationId))
            {
                throw new InvalidOperationException("Rename candidate evidence belongs to a different generation.");
            }

            var copied = preparation.Items.ToArray();
            if (copied.Select(item => item.File.Id).Distinct().Count() != copied.Length)
            {
                throw new InvalidOperationException("Duplicate candidate source.");
            }

            _renameCandidateState = preparation.State;
            _renameCandidates = copied;
            return Task.CompletedTask;
        }
    }

    private FontRenameCandidateReadContext RenameContext(FontRenameCandidateReadRequirements requirements)
    {
        var batch = _analysisBatches.Where(item => item.GenerationId == _publishedGenerationId && item.Status == AnalysisBatchStatus.Complete)
            .OrderByDescending(item => item.CompletedUtc).ThenByDescending(item => item.Id).FirstOrDefault();
        FontPublishedAnalysisContext? publication = batch is null ? null : new(batch.GenerationId, batch.Id, batch.EvidenceVersion, _inputs[batch.GenerationId]);
        return SqliteFontRepository.EvaluateRenameCandidateContext(publication, _renameCandidateState, requirements);
    }

    public int InitializeCallCount { get; private set; }

    public int RuntimeSnapshotLoadCallCount { get; private set; }

    public int InventoryLoadCallCount { get; private set; }

    public Func<CancellationToken, Task>? InitializationDelay { get; set; }

    /// <summary>Gets or sets a controlled delay after runtime Faces are captured, preserving the selected generation during a delayed read.</summary>
    public Func<CancellationToken, Task>? RuntimeSnapshotReadDelay { get; set; }

    public Action<OperationRequest>? BeforeOperationRequestCreate { get; set; }

    public Task<IReadOnlyList<OperationRequest>> LoadActiveRenameRequestsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return Task.FromResult<IReadOnlyList<OperationRequest>>(_operationRequests.Where(request => request.Kind == FontOperationKind.NormalizeNames && request.Status is OperationRequestStatus.Pending or OperationRequestStatus.Running).ToArray());
        }
    }

    public Task<IReadOnlyList<RenameItemRecord>> LoadRenameStateItemsAsync(CancellationToken cancellationToken)
    {
        if (ThrowOnFullRenameReads) throw new InvalidOperationException("GET must not load all execution evidence");
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return Task.FromResult<IReadOnlyList<RenameItemRecord>>(_maintenanceBatches.SelectMany(batch => batch.RenameItems.Select(item => item with { MaintenanceBatchId = batch.Id, OperationRequestId = batch.OperationRequestId })).ToArray());
        }
    }

    public Task<IReadOnlyList<OperationRequest>> LoadRenameStateRequestsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return Task.FromResult<IReadOnlyList<OperationRequest>>(_operationRequests.Where(request => request.Kind == FontOperationKind.NormalizeNames).ToArray());
        }
    }

    public IReadOnlyList<FontFileRecord> PublishedFiles
    {
        get
        {
            lock (_sync)
            {
                return _publishedGenerationId is long generationId ? _generations[generationId] : [];
            }
        }
    }

    public IReadOnlyList<OperationRequest> OperationRequests
    {
        get
        {
            lock (_sync)
            {
                return _operationRequests.ToArray();
            }
        }
    }

    public IReadOnlyList<PerformanceSample> PerformanceSamples
    {
        get
        {
            lock (_sync)
            {
                return _performanceSamples.ToArray();
            }
        }
    }

    public Task<OperationRequest?> GetLatestCompletedAnalysisOperationAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return Task.FromResult(_operationRequests.Where(request => request.Kind is FontOperationKind.Analyze or FontOperationKind.RebuildDerivedData && request.Status == OperationRequestStatus.Complete && request.ResultJson != null).OrderByDescending(request => request.CompletedUtc).ThenByDescending(request => request.Id).FirstOrDefault());
        }
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InitializeCallCount++;
        return InitializationDelay?.Invoke(cancellationToken) ?? Task.CompletedTask;
    }

    public Task<long> BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot input, IReadOnlyList<string> changeReasons, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            long generationId = ++_nextGenerationId;
            _inputs[generationId] = input;
            _buildingGenerations.Add(generationId);
            _generationCreatedUtc.Add(generationId, DateTimeOffset.UtcNow);
            _generations.Add(generationId, []);
            return Task.FromResult(generationId);
        }
    }

    public Task<Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot?> GetPublishedAnalysisInputAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return Task.FromResult(_publishedGenerationId is long id ? _inputs[id] : null);
        }
    }

    public Task WriteInventoryAsync(long generationId, IReadOnlyList<FontFileRecord> files, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_buildingGenerations.Contains(generationId))
            {
                throw new InvalidOperationException("Inventory can only be written to a building generation.");
            }

            if (files.Select(file => file.NormalizedPath).Distinct(StringComparer.Ordinal).Count() != files.Count)
            {
                throw new InvalidOperationException("A generation cannot contain duplicate normalized paths.");
            }

            _generations[generationId] = files.ToArray();
            return Task.CompletedTask;
        }
    }

    public Task<int> PruneObsoleteGenerationsAsync(int publishedGenerationRetentionCount, DateTimeOffset abandonedBeforeUtc, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(publishedGenerationRetentionCount);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            HashSet<long> retainedPublished = publishedGenerationRetentionCount == 0
                ? _generations.Keys.Where(id => !_buildingGenerations.Contains(id)).ToHashSet()
                : _generations.Keys.Where(id => !_buildingGenerations.Contains(id)).OrderByDescending(id => id).Take(publishedGenerationRetentionCount).ToHashSet();
            long[] obsolete = _generations.Keys
                .Where(id => id != _publishedGenerationId
                    && ((!_buildingGenerations.Contains(id) && !retainedPublished.Contains(id)) || (_buildingGenerations.Contains(id) && _generationCreatedUtc[id] < abandonedBeforeUtc))
                    && !_analysisBatches.Any(batch => batch.GenerationId == id))
                .ToArray();
            foreach (long id in obsolete)
            {
                _generations.Remove(id);
                _buildingGenerations.Remove(id);
                _generationCreatedUtc.Remove(id);
            }

            progress?.Report(100);
            return Task.FromResult(obsolete.Length);
        }
    }

    public Task<IReadOnlyList<RuntimeFontFace>> LoadPublishedRuntimeFacesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RuntimeSnapshotLoadCallCount++;
        lock (_sync)
        {
            if (_publishedGenerationId is not long generationId)
            {
                return Task.FromResult<IReadOnlyList<RuntimeFontFace>>([]);
            }

            return LoadRuntimeFacesCore(generationId, cancellationToken);
        }
    }

    public Task<IReadOnlyList<RuntimeFontFace>> LoadRuntimeFacesAsync(long generationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return LoadRuntimeFacesCore(generationId, cancellationToken);
        }
    }

    private Task<IReadOnlyList<RuntimeFontFace>> LoadRuntimeFacesCore(long generationId, CancellationToken cancellationToken)
    {
            RuntimeFontFace[] faces = _generations[generationId]
                .SelectMany(file => file.Faces.Select(face => new RuntimeFontFace(
                    face.Id,
                    file.Path,
                    face.FaceIndex,
                    face.FamilyName,
                    face.StyleName,
                    face.Names,
                    face.Weight,
                    face.Width,
                    face.Slant,
                    face.IsItalic,
                    face.IsFixedPitch,
                    face.Technology,
                    face.RetentionScore,
                    face.IsRuntimePreferred)
                {
                    NameRecords = face.NameRecords,
                    UnicodeCoverage = face.UnicodeCoverage
                }))
                .ToArray();
            return RuntimeSnapshotReadDelay is { } delay
                ? CompleteRuntimeSnapshotReadAsync(faces, delay, cancellationToken)
                : Task.FromResult<IReadOnlyList<RuntimeFontFace>>(faces);
    }

    private static async Task<IReadOnlyList<RuntimeFontFace>> CompleteRuntimeSnapshotReadAsync(
        IReadOnlyList<RuntimeFontFace> faces,
        Func<CancellationToken, Task> delay,
        CancellationToken cancellationToken)
    {
        await delay(cancellationToken);
        return faces;
    }

    public Task<IReadOnlyList<FontFileRecord>> LoadPublishedInventoryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<FontFileRecord> metadata = PublishedFiles
            .Select(file => file with { Faces = file.Faces.Select(face => face with { UnicodeCoverage = [] }).ToArray() })
            .ToArray();
        return Task.FromResult(metadata);
    }

    public Task<IReadOnlyList<FontFileRecord>> LoadPublishedInventoryForReuseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(PublishedFiles);
    }

    public Task<IReadOnlyList<FontFileRecord>> LoadInventoryAsync(long generationId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generationId);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            InventoryLoadCallCount++;
            return Task.FromResult(_generations.TryGetValue(generationId, out IReadOnlyList<FontFileRecord>? files) ? files : []);
        }
    }

    public Task<bool> HasCompleteAnalysisAsync(long generationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return Task.FromResult(_analysisBatches.Any(batch => batch.GenerationId == generationId && batch.Status == AnalysisBatchStatus.Complete));
        }
    }

    public Task<string?> LoadReusableDeepEvidenceAsync(FontFileRecord file, string evidenceKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            foreach (FontFileRecord candidate in _generations.Values.SelectMany(files => files).OrderByDescending(candidate => candidate.Id))
            {
                if (candidate.PhysicalIdentity == file.PhysicalIdentity
                    && candidate.Size == file.Size
                    && candidate.ModifiedUtc == file.ModifiedUtc
                    && candidate.Sha256.Span.SequenceEqual(file.Sha256.Span)
                    && _deepEvidence.TryGetValue(candidate.Id, out var cached)
                    && cached.EvidenceKey == evidenceKey)
                {
                    return Task.FromResult<string?>(cached.Payload);
                }
            }

            return Task.FromResult<string?>(null);
        }
    }

    public Task SaveDeepEvidenceAsync(long fileId, string evidenceKey, string payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _deepEvidence[fileId] = (evidenceKey, payload);
            return Task.CompletedTask;
        }
    }

    public Task<AnalysisBatch?> GetLatestCompleteAnalysisAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            AnalysisBatch? result = _analysisBatches
                .Where(batch => batch.Status == AnalysisBatchStatus.Complete && batch.GenerationId == _publishedGenerationId)
                .OrderByDescending(batch => batch.CompletedUtc)
                .ThenByDescending(batch => batch.Id)
                .FirstOrDefault();
            return Task.FromResult(result);
        }
    }

    public Task<AnalysisBatch?> GetAnalysisBatchAsync(long batchId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return Task.FromResult(_analysisBatches.FirstOrDefault(batch => batch.Id == batchId));
        }
    }

    public Task<FontRepositoryPage<AnalysisBatch>> LoadAnalysisBatchPageAsync(long offset, int pageSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 200);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            AnalysisBatch[] ordered = _analysisBatches
                .Where(batch => !_buildingGenerations.Contains(batch.GenerationId))
                .OrderByDescending(batch => batch.CompletedUtc ?? batch.StartedUtc)
                .ThenByDescending(batch => batch.Id)
                .ToArray();
            IReadOnlyList<AnalysisBatch> page = offset >= ordered.Length
                ? []
                : ordered.Skip((int)offset).Take(pageSize).ToArray();
            return Task.FromResult(new FontRepositoryPage<AnalysisBatch>(page, ordered.Length));
        }
    }

    public Task<AnalysisBatch> PublishAnalysisAsync(AnalysisBatch batch, CancellationToken cancellationToken, FontRenameCandidatePreparation? preparedCandidates = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (batch.Id != 0)
            {
                throw new ArgumentOutOfRangeException(nameof(batch), "A new analysis batch must not have a persisted identifier.");
            }

            if (!_buildingGenerations.Contains(batch.GenerationId) || batch.Status != AnalysisBatchStatus.Complete)
            {
                throw new InvalidOperationException("Only a completed analysis of a building generation can be published.");
            }

            if (preparedCandidates is not null)
            {
                SqliteFontRepository.ValidatePublishedCandidatePreparation(batch, preparedCandidates);
                _ = preparedCandidates.Items.ToDictionary(item => item.File.Id);
            }

            AnalysisBatch persisted = batch with { Id = ++_nextAnalysisBatchId };
            _renameCandidateState = preparedCandidates is null ? null : preparedCandidates.State with { AnalysisBatchId = persisted.Id };
            _renameCandidates = preparedCandidates?.Items.ToArray() ?? [];
            if (_generations.TryGetValue(batch.GenerationId, out IReadOnlyList<FontFileRecord>? inventory))
            {
                Dictionary<long, FontFaceAnalysisRecord> scores = batch.FaceScores.ToDictionary(face => face.FaceId);
                _generations[batch.GenerationId] = inventory.Select(file => file with
                {
                    Faces = file.Faces.Select(face => scores.TryGetValue(face.Id, out FontFaceAnalysisRecord? score)
                        ? face with { RetentionScore = score.RetentionScore, IsRuntimePreferred = score.IsRuntimePreferred }
                        : face).ToArray()
                }).ToArray();
            }

            _analysisBatches.Add(persisted);
            _buildingGenerations.Remove(batch.GenerationId);
            _publishedGenerationId = batch.GenerationId;
            return Task.FromResult(persisted);
        }
    }

    public Task MarkAnalysisStaleAsync(long generationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            for (int index = 0; index < _analysisBatches.Count; index++)
            {
                AnalysisBatch batch = _analysisBatches[index];
                if (batch.GenerationId == generationId && batch.Status == AnalysisBatchStatus.Complete)
                {
                    _analysisBatches[index] = batch with { Status = AnalysisBatchStatus.Stale };
                }
            }

            return Task.CompletedTask;
        }
    }

    public Task<int> PruneAnalysisBatchesAsync(int retentionCount, CancellationToken cancellationToken)
    {
        BeforePrune?.Invoke(retentionCount);
        ArgumentOutOfRangeException.ThrowIfNegative(retentionCount);
        cancellationToken.ThrowIfCancellationRequested();
        if (retentionCount == 0)
        {
            return Task.FromResult(0);
        }

        lock (_sync)
        {
            HashSet<long> newest = _analysisBatches
                .Where(batch => batch.Status is AnalysisBatchStatus.Complete or AnalysisBatchStatus.Stale or AnalysisBatchStatus.Failed)
                .OrderByDescending(batch => batch.CompletedUtc ?? batch.StartedUtc)
                .ThenByDescending(batch => batch.Id)
                .Take(retentionCount)
                .Select(batch => batch.Id)
                .ToHashSet();
            long? currentValid = _analysisBatches
                .Where(batch => batch.GenerationId == _publishedGenerationId && batch.Status == AnalysisBatchStatus.Complete)
                .OrderByDescending(batch => batch.CompletedUtc)
                .ThenByDescending(batch => batch.Id)
                .Select(batch => (long?)batch.Id)
                .FirstOrDefault();
            HashSet<long> active = _operationRequests
                .Where(request => request.Status is OperationRequestStatus.Pending or OperationRequestStatus.Running)
                .Where(request => request.AnalysisBatchId.HasValue)
                .Select(request => request.AnalysisBatchId!.Value)
                .ToHashSet();
            HashSet<long> toRemove = _analysisBatches
                .Where(batch => batch.Status is AnalysisBatchStatus.Complete or AnalysisBatchStatus.Stale or AnalysisBatchStatus.Failed)
                .Where(batch => !newest.Contains(batch.Id) && batch.Id != currentValid && !active.Contains(batch.Id))
                .Select(batch => batch.Id)
                .ToHashSet();
            for (int index = 0; index < _operationRequests.Count; index++)
            {
                OperationRequest request = _operationRequests[index];
                if (request.AnalysisBatchId is long batchId && toRemove.Contains(batchId))
                {
                    _operationRequests[index] = request with { AnalysisBatchId = null };
                }
            }

            int removed = _analysisBatches.RemoveAll(batch => toRemove.Contains(batch.Id));
            return Task.FromResult(removed);
        }
    }

    public Task RecoverRenameCandidateRefreshAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            for (int i = 0; i < _operationRequests.Count; i++)
            {
                var request = _operationRequests[i];
                if (request.Kind == FontOperationKind.RefreshRenameCandidates && request.Status == OperationRequestStatus.Running)
                {
                    _operationRequests[i] = request with { Status = OperationRequestStatus.Pending, ClaimedUtc = null };
                    if (_renameCandidateState?.OperationRequestId == request.Id && _renameCandidateState.Status == FontRenameCandidateGenerationStatus.Running)
                        _renameCandidateState = _renameCandidateState with { Status = FontRenameCandidateGenerationStatus.Pending };
                }
                else if (request.Kind == FontOperationKind.Analyze && request.Status == OperationRequestStatus.Running
                    && _renameCandidateState?.OperationRequestId == request.Id
                    && _renameCandidateState.Status is FontRenameCandidateGenerationStatus.Pending or FontRenameCandidateGenerationStatus.Running)
                {
                    const string error = "The analysis task was interrupted during rename candidate generation.";
                    _operationRequests[i] = request with { Status = OperationRequestStatus.Failed, CompletedUtc = DateTimeOffset.UtcNow, Error = error };
                    _renameCandidateState = _renameCandidateState with { Status = FontRenameCandidateGenerationStatus.Stale, Error = error };
                }
            }

            return Task.CompletedTask;
        }
    }

    /// <inheritdoc/>
    public Task<OperationRequest?> CreateRenameCandidateRefreshAsync(OperationRequest request, FontNamingRules rules, FontRenameCandidateReadRequirements requirements, bool onlyIfNeeded, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var existing = _operationRequests.FirstOrDefault(item => item.IdempotencyKey == request.IdempotencyKey);
            if (existing is not null)
            {
                if (existing.Kind != FontOperationKind.RefreshRenameCandidates) throw new InvalidOperationException("The idempotency key belongs to another operation.");
                return Task.FromResult<OperationRequest?>(existing);
            }

            var context = RenameContext(requirements);
            var proposed = SqliteFontRepository.ParseRenameRefresh(request.RequestJson);
            if (!SqliteFontRepository.RefreshMatchesPublication(context, proposed, requirements) || proposed.RuleFingerprint != rules.GetFingerprint())
            {
                if (onlyIfNeeded) return Task.FromResult<OperationRequest?>(null);
                throw new InvalidOperationException("The source analysis changed.");
            }

            bool same = context.State is not null && context.State.Status != FontRenameCandidateGenerationStatus.Stale;
            if (onlyIfNeeded && same)
            {
                var owner = _operationRequests.FirstOrDefault(item => item.Id == context.State!.OperationRequestId);
                if (context.State!.Status is FontRenameCandidateGenerationStatus.Pending or FontRenameCandidateGenerationStatus.Running
                    && owner?.Status is OperationRequestStatus.Complete or OperationRequestStatus.Failed or OperationRequestStatus.Cancelled)
                {
                    _renameCandidateState = context.State with { Status = FontRenameCandidateGenerationStatus.Failed, Error = owner.Error ?? "The candidate calculation ended without a committed result." };
                }

                return Task.FromResult<OperationRequest?>(null);
            }
            long? root = same && context.State!.OperationRequestId is long active && _operationRequests.Any(item => item.Id == active && item.Status is OperationRequestStatus.Pending or OperationRequestStatus.Running) ? active : null;
            var persisted = request with { Id = ++_nextOperationRequestId, RequestJson = System.Text.Json.JsonSerializer.Serialize(proposed with { CalculationRequestId = root }, System.Text.Json.JsonSerializerOptions.Web) };
            _operationRequests.Add(persisted);
            if (root is null)
            {
                _renameCandidateState = new(proposed.GenerationId, proposed.AnalysisBatchId, rules, rules.GetFingerprint(), proposed.CalculatorVersion, FontRenameCandidateGenerationStatus.Pending, context.State?.GeneratedUtc, persisted.Id, null);
            }

            return Task.FromResult<OperationRequest?>(persisted);
        }
    }

    public Task<IReadOnlyList<OperationRequest>> ClaimRenameCandidateRefreshGroupAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var pending = _operationRequests.Where(item => item.Kind == FontOperationKind.RefreshRenameCandidates && item.Status == OperationRequestStatus.Pending).OrderBy(item => item.Id).ToArray();
            if (pending.Length == 0) return Task.FromResult<IReadOnlyList<OperationRequest>>([]);
            long root = SqliteFontRepository.ParseRenameRefresh(pending[0].RequestJson).CalculationRequestId ?? pending[0].Id;
            var group = pending.Where(item => (SqliteFontRepository.ParseRenameRefresh(item.RequestJson).CalculationRequestId ?? item.Id) == root)
                .Select(item => item with { Status = OperationRequestStatus.Running, ClaimedUtc = DateTimeOffset.UtcNow }).ToArray();
            foreach (var request in group) _operationRequests[_operationRequests.FindIndex(item => item.Id == request.Id)] = request;
            return Task.FromResult<IReadOnlyList<OperationRequest>>(group);
        }
    }

    public Task<OperationRequest> CreateOperationRequestAsync(OperationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            OperationRequest? existing = _operationRequests.FirstOrDefault(candidate => candidate.IdempotencyKey == request.IdempotencyKey);
            if (existing is not null)
            {
                return Task.FromResult(existing);
            }

            BeforeOperationRequestCreate?.Invoke(request);
            OperationRequest persisted = request with { Id = ++_nextOperationRequestId };
            _operationRequests.Add(persisted);
            return Task.FromResult(persisted);
        }
    }

    public Task<OperationRequest?> ClaimNextOperationAsync(FontOperationKind kind, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            int index = _operationRequests.FindIndex(request => request.Kind == kind && request.Status == OperationRequestStatus.Pending);
            if (index < 0)
            {
                return Task.FromResult<OperationRequest?>(null);
            }

            OperationRequest claimed = _operationRequests[index] with
            {
                Status = OperationRequestStatus.Running,
                ClaimedUtc = DateTimeOffset.UtcNow
            };
            _operationRequests[index] = claimed;
            return Task.FromResult<OperationRequest?>(claimed);
        }
    }

    public Task UpdateOperationAsync(OperationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            int index = _operationRequests.FindIndex(candidate => candidate.Id == request.Id);
            if (index < 0)
            {
                throw new InvalidOperationException("The operation request does not exist.");
            }

            _operationRequests[index] = request;
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<FontOperationKind>> LoadPendingOperationKindsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return Task.FromResult<IReadOnlyList<FontOperationKind>>(_operationRequests.Where(request => request.Status == OperationRequestStatus.Pending).Select(request => request.Kind).Distinct().Order().ToArray());
        }
    }

    public Task<OperationRequest?> GetOperationRequestAsync(long requestId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return Task.FromResult(_operationRequests.FirstOrDefault(request => request.Id == requestId));
        }
    }

    public Task<OperationRequest?> GetOperationRequestByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return Task.FromResult(_operationRequests.FirstOrDefault(request => request.IdempotencyKey == idempotencyKey.Trim()));
        }
    }

    public Task<MaintenanceBatch> SaveMaintenanceBatchAsync(MaintenanceBatch batch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            MaintenanceBatch persisted = batch with { Id = ++_nextMaintenanceBatchId };
            _maintenanceBatches.Add(persisted);
            return Task.FromResult(persisted);
        }
    }

    public Task UpdateMaintenanceBatchAsync(MaintenanceBatch batch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            int index = _maintenanceBatches.FindIndex(candidate => candidate.Id == batch.Id);
            if (index < 0)
            {
                throw new InvalidOperationException("The maintenance batch does not exist.");
            }

            _maintenanceBatches[index] = batch;
            return Task.CompletedTask;
        }
    }

    public Task ImportMaintenanceBatchAsync(
        MaintenanceBatch batch,
        long analysisBatchId,
        string idempotencyKey,
        string requestJson,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(analysisBatchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestJson);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            MaintenanceBatch? existing = _maintenanceBatches.FirstOrDefault(candidate => candidate.Id == batch.Id);
            if (existing is null)
            {
                _maintenanceBatches.Add(batch);
                _nextMaintenanceBatchId = Math.Max(_nextMaintenanceBatchId, batch.Id);
            }
            else if (existing.OperationRequestId != batch.OperationRequestId
                || existing.Kind != batch.Kind
                || existing.StartedUtc != batch.StartedUtc)
            {
                throw new InvalidOperationException("The imported maintenance batch conflicts with an existing identity.");
            }
            else
            {
                int index = _maintenanceBatches.IndexOf(existing);
                _maintenanceBatches[index] = batch;
            }

            return Task.CompletedTask;
        }
    }

    public Task<MaintenanceBatch?> GetMaintenanceBatchAsync(long batchId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return Task.FromResult(_maintenanceBatches.FirstOrDefault(batch => batch.Id == batchId));
        }
    }

    public Task<MaintenanceBatch?> GetMaintenanceBatchByOperationRequestAsync(
        long operationRequestId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            return Task.FromResult(_maintenanceBatches.FirstOrDefault(batch => batch.OperationRequestId == operationRequestId));
        }
    }

    public Task<FontRepositoryPage<MaintenanceBatch>> LoadMaintenanceBatchPageAsync(long offset, int pageSize, IReadOnlySet<FontOperationKind>? kinds, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 200);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            IEnumerable<MaintenanceBatch> filtered = kinds is null
                ? _maintenanceBatches
                : _maintenanceBatches.Where(batch => kinds.Contains(batch.Kind));
            MaintenanceBatch[] ordered = filtered
                .OrderByDescending(batch => batch.CompletedUtc ?? batch.StartedUtc)
                .ThenByDescending(batch => batch.Id)
                .ToArray();
            IReadOnlyList<MaintenanceBatch> page = offset >= ordered.Length
                ? []
                : ordered.Skip((int)offset).Take(pageSize).ToArray();
            return Task.FromResult(new FontRepositoryPage<MaintenanceBatch>(page, ordered.Length));
        }
    }

    public Task<FontRepositoryPage<QuarantineItemRecord>> LoadQuarantineItemPageAsync(long offset, int pageSize, IReadOnlySet<QuarantineItemStatus> statuses, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 200);
        ArgumentNullException.ThrowIfNull(statuses);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            QuarantineItemRecord[] items = _maintenanceBatches
                .Where(batch => batch.Kind == FontOperationKind.Quarantine)
                .SelectMany(batch => batch.Items.Select(item => item with { MaintenanceBatchId = batch.Id }))
                .GroupBy(item => item.SourcePath, StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(item => item.Id).ThenByDescending(item => item.MaintenanceBatchId).First())
                .Where(item => statuses.Contains(item.Status))
                .OrderByDescending(item => item.Id)
                .ThenByDescending(item => item.MaintenanceBatchId)
                .ToArray();
            IReadOnlyList<QuarantineItemRecord> page = offset >= items.Length
                ? []
                : items.Skip((int)offset).Take(pageSize).ToArray();
            return Task.FromResult(new FontRepositoryPage<QuarantineItemRecord>(page, items.Length));
        }
    }

    public Task<FontDatabaseHealth> GetDatabaseHealthAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new FontDatabaseHealth
        {
            DatabasePath = ":memory:",
            SchemaVersion = SqliteSchema.CurrentVersion,
            JournalMode = "memory",
            PublishedGenerationId = _publishedGenerationId,
            CheckedUtc = DateTimeOffset.UtcNow
        });
    }

    public Task SavePerformanceSampleAsync(PerformanceSample sample, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _performanceSamples.Add(sample);
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<PerformanceSample>> LoadRecentPerformanceSamplesAsync(int maximumCount, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            IReadOnlyList<PerformanceSample> result = _performanceSamples
                .OrderByDescending(sample => sample.RecordedUtc)
                .Take(maximumCount)
                .ToArray();
            return Task.FromResult(result);
        }
    }
}
