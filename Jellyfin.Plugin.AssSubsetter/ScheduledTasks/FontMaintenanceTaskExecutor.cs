using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;
using Jellyfin.Plugin.AssSubsetter.Fonts.Api;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;
using Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;
using Jellyfin.Plugin.AssSubsetter.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.AssSubsetter.ScheduledTasks;

/// <summary>Serializes inventory analysis and file maintenance while preserving durable operation-request lifecycles.</summary>
public sealed partial class FontMaintenanceTaskExecutor : IFontMaintenanceTaskExecutor, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IFontRepository _repository;
    private readonly ILogger<FontMaintenanceTaskExecutor> _logger;
    private readonly FontOperationRequestStore _requestStore;
    private readonly TimeProvider _timeProvider;
    private readonly IFontFilenameNormalizationExecutor _filenameNormalizer;
    private readonly Func<IProgress<double>?, bool, CancellationToken, Task<FontInventoryScanResult>> _refresh;
    private readonly Func<long, IProgress<double>?, CancellationToken, Task<AnalysisBatch>> _analyze;
    private readonly Func<AnalysisBatch, FontRenameCandidatePreparation?, CancellationToken, Task<AnalysisBatch>> _publish;
    private readonly Func<FontInventoryScanResult, IProgress<double>?, CancellationToken, Task> _validate;
    private readonly Func<AnalysisBatch, CancellationToken, Task<FontRenameCandidatePreparation>>? _prepare;
    private readonly Func<long, CancellationToken, Task>? _repairCandidates;
    private readonly Func<OperationRequest, IProgress<double>?, CancellationToken, Task<MaintenanceBatch>> _quarantine;
    private readonly Func<OperationRequest, CancellationToken, Task<MaintenanceBatch>> _restore;
    private readonly Func<IProgress<double>?, CancellationToken, Task<int>> _purge;
    private readonly object _taskLifetime = new();
    private readonly Action? _signalPendingRequests;
    private readonly Func<long, CancellationToken, Task<FontRenameCandidateState>>? _rebuildCandidates;
    private readonly Func<FontRenameCandidateReadRequirements>? _candidateRequirements;
    private readonly Func<CancellationToken, Task<OperationRequest?>>? _ensureCandidates;
    private bool _taskActive;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="FontMaintenanceTaskExecutor"/> class.</summary>
    /// <param name="repository">The durable font catalog and operation queue.</param>
    /// <param name="requestStore">The immutable idempotent request factory.</param>
    /// <param name="fontCacheManager">The published inventory and runtime-index owner.</param>
    /// <param name="analyzer">The duplicate analysis module.</param>
    /// <param name="quarantineManager">The recoverable quarantine lifecycle module.</param>
    /// <param name="filenameNormalizer">The explicit filename-normalization executor.</param>
    /// <param name="timeProvider">The UTC clock used for terminal operation states.</param>
    /// <param name="candidateQueue">Accepts candidate refreshes after context drift.</param>
    /// <param name="refreshService">Signals candidate repair after task completion.</param>
    /// <param name="candidateService">Prepares current persisted rename candidates.</param>
    /// <param name="logger">Records task stages, identities and file outcomes.</param>
    /// <param name="requestDispatcher">Signals durable operation scheduling after the current task releases.</param>
    public FontMaintenanceTaskExecutor(
        IFontRepository repository,
        FontOperationRequestStore requestStore,
        FontCacheManager fontCacheManager,
        FontDuplicateAnalyzer analyzer,
        FontQuarantineManager quarantineManager,
        IFontFilenameNormalizationExecutor filenameNormalizer,
        TimeProvider timeProvider,
        IFontOperationRequestDispatcher requestDispatcher,
        FontRenameCandidateService candidateService,
        FontRenameCandidateRefreshQueue candidateQueue,
        FontRenameCandidateRefreshService refreshService,
        ILogger<FontMaintenanceTaskExecutor> logger)
        : this(
            repository,
            requestStore,
            timeProvider,
            filenameNormalizer,
            fontCacheManager.StageInventoryAsync,
            analyzer.AnalyzeAsync,
            quarantineManager.QuarantineAsync,
            quarantineManager.RestoreAsync,
            (progress, token) => quarantineManager.PurgeExpiredAsync(token, progress),
            publish: (batch, prepared, token) => fontCacheManager.PublishAnalyzedGenerationAsync(batch, token, prepared),
            validate: (scan, progress, token) => fontCacheManager.ValidatePublicationInputsAsync(scan, token, progress),
            prepare: candidateService.PrepareForPublicationAsync,
            repairCandidates: async (requestId, token) =>
            {
                var context = await repository.GetRenameCandidateContextAsync(candidateService.ReadRequirements(), token).ConfigureAwait(false);
                if (!context.IsCurrent && !(context.State?.Status is FontRenameCandidateGenerationStatus.Pending or FontRenameCandidateGenerationStatus.Running
                    && context.State.OperationRequestId > requestId))
                {
                    try
                    {
                        await candidateService.RebuildCurrentAsync(requestId, token).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException)
                    {
                        var latest = await repository.GetRenameCandidateContextAsync(candidateService.ReadRequirements(), token).ConfigureAwait(false);
                        if (latest.State?.OperationRequestId > requestId)
                        {
                            return;
                        }

                        throw;
                    }
                }
            },
            rebuildCandidates: candidateService.RebuildCurrentAsync,
            candidateRequirements: candidateService.ReadRequirements,
            ensureCandidates: candidateQueue.EnsureCurrentAsync,
            logger: logger)
    {
        _signalPendingRequests = () =>
        {
            requestDispatcher.Signal();
            refreshService.Signal();
        };
    }

    internal FontMaintenanceTaskExecutor(
        IFontRepository repository,
        FontOperationRequestStore requestStore,
        TimeProvider timeProvider,
        IFontFilenameNormalizationExecutor filenameNormalizer,
        Func<IProgress<double>?, bool, CancellationToken, Task<FontInventoryScanResult>> refresh,
        Func<long, IProgress<double>?, CancellationToken, Task<AnalysisBatch>> analyze,
        Func<OperationRequest, IProgress<double>?, CancellationToken, Task<MaintenanceBatch>> quarantine,
        Func<OperationRequest, CancellationToken, Task<MaintenanceBatch>> restore,
        Func<IProgress<double>?, CancellationToken, Task<int>> purge,
        Func<AnalysisBatch, FontRenameCandidatePreparation?, CancellationToken, Task<AnalysisBatch>> publish,
        Action? signalPendingRequests = null,
        Func<FontInventoryScanResult, IProgress<double>?, CancellationToken, Task>? validate = null,
        Func<AnalysisBatch, CancellationToken, Task<FontRenameCandidatePreparation>>? prepare = null,
        Func<long, CancellationToken, Task>? repairCandidates = null,
        Func<long, CancellationToken, Task<FontRenameCandidateState>>? rebuildCandidates = null,
        Func<FontRenameCandidateReadRequirements>? candidateRequirements = null,
        Func<CancellationToken, Task<OperationRequest?>>? ensureCandidates = null,
        ILogger<FontMaintenanceTaskExecutor>? logger = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _logger = logger ?? NullLogger<FontMaintenanceTaskExecutor>.Instance;
        _requestStore = requestStore ?? throw new ArgumentNullException(nameof(requestStore));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _filenameNormalizer = filenameNormalizer ?? throw new ArgumentNullException(nameof(filenameNormalizer));
        _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        _analyze = analyze ?? throw new ArgumentNullException(nameof(analyze));
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _validate = validate ?? ((_, _, _) => Task.CompletedTask);
        _prepare = prepare;
        _repairCandidates = repairCandidates;
        _rebuildCandidates = rebuildCandidates;
        _candidateRequirements = candidateRequirements;
        _ensureCandidates = ensureCandidates;
        _quarantine = quarantine ?? throw new ArgumentNullException(nameof(quarantine));
        _restore = restore ?? throw new ArgumentNullException(nameof(restore));
        _purge = purge ?? throw new ArgumentNullException(nameof(purge));
        _signalPendingRequests = signalPendingRequests;
    }

    /// <inheritdoc/>
    public bool IsBusy
    {
        get
        {
            lock (_taskLifetime)
            {
                return _taskActive;
            }
        }
    }

    /// <inheritdoc/>
    public Task<FontTaskExecutionResult> ExecutePendingOperationAsync(FontOperationKind kind, IProgress<double> progress, CancellationToken cancellationToken)
        => kind switch
        {
            FontOperationKind.Analyze or FontOperationKind.RebuildDerivedData => RunAnalysisAsync(progress, allowScheduledWork: false, cancellationToken),
            FontOperationKind.RefreshRenameCandidates => RunCandidateRefreshAsync(cancellationToken),
            FontOperationKind.Quarantine => RunQuarantineAsync(progress, allowScheduledWork: false, cancellationToken),
            FontOperationKind.Purge => RunPurgeAsync(progress, allowScheduledWork: false, cancellationToken),
            FontOperationKind.NormalizeNames or FontOperationKind.RollbackNames => RunFilenameOperationAsync(progress, allowScheduledWork: false, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "No scheduled font operation handles this request.")
        };

    /// <summary>Rejects new tasks while allowing the current operation to finish.</summary>
    public void Dispose()
    {
        lock (_taskLifetime)
        {
            _disposed = true;
        }
    }

    /// <summary>Deletes retained reports and unused inventory only outside active analysis and maintenance.</summary>
    /// <param name="retentionCount">Number of reports and published inventory generations to keep; zero retains both.</param>
    /// <param name="progress">Receives cleanup progress from zero through one hundred.</param>
    /// <param name="cancellationToken">Cancels pruning.</param>
    /// <returns>Counts of deleted rows, or a skipped result when another task is active.</returns>
    public async Task<FontHistoryPruneResult> PruneHistoryAsync(int retentionCount, IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(retentionCount);
        ArgumentNullException.ThrowIfNull(progress);
        if (!TryEnterTask(cancellationToken))
        {
            return new FontHistoryPruneResult(0, 0, true);
        }

        try
        {
            _logger.LogInformation("[AssSubsetter] History pruning reports. RetentionCount: {RetentionCount}.", retentionCount);
            int batches = await _repository.PruneAnalysisBatchesAsync(retentionCount, cancellationToken).ConfigureAwait(false);
            progress.Report(3);
            _logger.LogInformation("[AssSubsetter] History pruning inventory. DeletedBatches: {DeletedBatches}.", batches);
            DateTimeOffset abandonedBeforeUtc = _timeProvider.GetUtcNow().AddDays(-1);
            int generations = await _repository.PruneObsoleteGenerationsAsync(
                retentionCount,
                abandonedBeforeUtc,
                new ScaledProgress(progress, 3, 99),
                cancellationToken).ConfigureAwait(false);
            return new FontHistoryPruneResult(batches, generations, false);
        }
        finally
        {
            ReleaseTask();
        }
    }

    /// <summary>Stages inventory, analyzes that exact generation, and publishes only a complete result.</summary>
    /// <param name="progress">Receives percentage completion from zero through one hundred.</param>
    /// <param name="cancellationToken">A token that cancels refresh or analysis.</param>
    /// <returns>The persisted analysis batch summary.</returns>
    public Task<FontTaskExecutionResult> RefreshInventoryAndAnalyzeAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => RunAnalysisAsync(progress, allowScheduledWork: true, cancellationToken);

    private async Task<FontTaskExecutionResult> RunAnalysisAsync(IProgress<double> progress, bool allowScheduledWork, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        progress.Report(0);
        if (!TryEnterTask(cancellationToken))
        {
            return new FontTaskExecutionResult(null, "task-active");
        }

        try
        {
            await _filenameNormalizer.ReconcileAsync(cancellationToken).ConfigureAwait(false);
            OperationRequest? request = await ClaimAnalysisOrRebuildRequestAsync(allowScheduledWork, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return new FontTaskExecutionResult(null, "request-missing");
            }

            try
            {
                var stopwatch = Stopwatch.StartNew();
                LogStage(request, "inventory-scan", stopwatch);
                FontInventoryScanResult scan = await _refresh(new ScaledProgress(progress, 0, 60), request.Kind == FontOperationKind.RebuildDerivedData, cancellationToken).ConfigureAwait(false);
                long generationId = scan.GenerationId;
                if (!scan.HasChanges)
                {
                    LogStage(request, "validate-reused-inputs", stopwatch);
                    await _validate(scan, new ScaledProgress(progress, 60, 95), cancellationToken).ConfigureAwait(false);
                    AnalysisBatch current = await _repository.GetLatestCompleteAnalysisAsync(cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("The reused analysis is no longer available.");
                    var health = await _repository.GetDatabaseHealthAsync(cancellationToken).ConfigureAwait(false);
                    var publishedInput = await _repository.GetPublishedAnalysisInputAsync(cancellationToken).ConfigureAwait(false);
                    if (current.GenerationId != generationId || health.PublishedGenerationId != generationId
                        || current.EvidenceVersion != FontDuplicateAnalyzer.CurrentEvidenceVersion || publishedInput != scan.Input)
                    {
                        throw new InvalidOperationException("The publication changed before its analysis could be reused.");
                    }

                    if (_repairCandidates is not null)
                    {
                        LogStage(request, "repair-rename-candidates", stopwatch);
                        await _repairCandidates(request.Id, cancellationToken).ConfigureAwait(false);
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    FontScanOutcomeDto outcome = new(generationId, current.Id, false, scan.ChangeReasons);
                    await CompleteRequestAsync(request with { ResultJson = JsonSerializer.Serialize(outcome, JsonOptions) }, OperationRequestStatus.Complete, null).ConfigureAwait(false);
                    _logger.LogInformation("[AssSubsetter] Analysis reused. RequestId: {RequestId}; GenerationId: {GenerationId}; BatchId: {BatchId}; ElapsedMs: {ElapsedMs}.", request.Id, generationId, current.Id, stopwatch.ElapsedMilliseconds);
                    progress.Report(100);
                    return new FontTaskExecutionResult(current.Id, "no-changes", request.Id) { ScanResult = outcome };
                }

                cancellationToken.ThrowIfCancellationRequested();
                progress.Report(60);
                LogStage(request, "duplicate-analysis", stopwatch);
                AnalysisBatch batch = await _analyze(generationId, new ScaledProgress(progress, 60, 85), cancellationToken).ConfigureAwait(false);
                if (batch.Status == AnalysisBatchStatus.Complete)
                {
                    progress.Report(85);
                    LogStage(request, "prepare-rename-candidates", stopwatch);
                    var prepared = _prepare is null ? null : await _prepare(batch, cancellationToken).ConfigureAwait(false);
                    progress.Report(90);
                    LogStage(request, "validate-publication-inputs", stopwatch);
                    await _validate(scan, new ScaledProgress(progress, 90, 97), cancellationToken).ConfigureAwait(false);
                    progress.Report(97);
                    LogStage(request, "publish-analysis", stopwatch);
                    batch = await _publish(batch, prepared, cancellationToken).ConfigureAwait(false);
                    progress.Report(99);
                    _logger.LogInformation("[AssSubsetter] Analysis published. RequestId: {RequestId}; GenerationId: {GenerationId}; BatchId: {BatchId}; ChangeReasons: {ChangeReasons}; Relations: {RelationCount}; RenameCandidates: {CandidateCount}; ElapsedMs: {ElapsedMs}.", request.Id, generationId, batch.Id, string.Join(",", scan.ChangeReasons), batch.Relations.Count, prepared?.Items.Count ?? 0, stopwatch.ElapsedMilliseconds);
                }

                OperationRequestStatus status = batch.Status == AnalysisBatchStatus.Complete
                    ? OperationRequestStatus.Complete
                    : OperationRequestStatus.Failed;
                await CompleteRequestAsync(
                    request with { ResultJson = status == OperationRequestStatus.Complete ? JsonSerializer.Serialize(new FontScanOutcomeDto(generationId, batch.Id, true, scan.ChangeReasons), JsonOptions) : null },
                    status,
                    status == OperationRequestStatus.Failed ? "The analysis batch did not complete." : null).ConfigureAwait(false);
                if (status == OperationRequestStatus.Complete)
                {
                    progress.Report(100);
                }

                return new FontTaskExecutionResult(
                    batch.Id,
                    status == OperationRequestStatus.Complete ? "analyzed" : batch.Status.ToString(),
                    request.Id) { ScanResult = status == OperationRequestStatus.Complete ? new FontScanOutcomeDto(generationId, batch.Id, true, scan.ChangeReasons) : null };
            }
            catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
            {
                await CompleteRequestAsync(request, OperationRequestStatus.Cancelled, "The scheduled task was cancelled.").ConfigureAwait(false);
                AddOperationRequestIdentifiers(exception, request);
                throw;
            }
            catch (Exception exception)
            {
                await CompleteRequestAsync(request, OperationRequestStatus.Failed, exception.Message).ConfigureAwait(false);
                AddOperationRequestIdentifiers(exception, request);
                throw;
            }
        }
        finally
        {
            ReleaseTask();
        }
    }

    /// <summary>Consumes one explicit request or synthesizes one request from the latest current complete analysis.</summary>
    /// <param name="progress">Receives per-file quarantine progress.</param>
    /// <param name="cancellationToken">A token that cancels before or during quarantine.</param>
    /// <returns>The maintenance batch or safe-skip summary.</returns>
    public Task<FontTaskExecutionResult> QuarantineDuplicatesAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => RunQuarantineAsync(progress, allowScheduledWork: true, cancellationToken);

    private async Task<FontTaskExecutionResult> RunQuarantineAsync(IProgress<double> progress, bool allowScheduledWork, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (!TryEnterTask(cancellationToken))
        {
            return new FontTaskExecutionResult(null, "task-active");
        }

        try
        {
            OperationRequest? request = await _repository.ClaimNextOperationAsync(
                FontOperationKind.Quarantine,
                cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                if (!allowScheduledWork)
                {
                    return new FontTaskExecutionResult(null, "request-missing");
                }

                AnalysisBatch? analysis = await _repository.GetLatestCompleteAnalysisAsync(cancellationToken).ConfigureAwait(false);
                if (analysis is null)
                {
                    return new FontTaskExecutionResult(null, "analysis-missing");
                }

                IReadOnlyList<FontFileRecord> inventory = await _repository.LoadPublishedInventoryAsync(cancellationToken).ConfigureAwait(false);
                if (!IsCurrent(analysis, inventory))
                {
                    return new FontTaskExecutionResult(analysis.Id, "analysis-stale");
                }

                long[] selectedFileIds = analysis.Groups
                    .SelectMany(group => group.Members)
                    .Where(member => member.Decision == DuplicateGroupMemberDecision.QuarantineCandidate)
                    .Select(member => member.FileId)
                    .Distinct()
                    .Order()
                    .ToArray();
                if (selectedFileIds.Length == 0)
                {
                    return new FontTaskExecutionResult(analysis.Id, "no-candidates");
                }

                OperationRequest scheduled = await _requestStore.CreateAsync(
                    FontOperationKind.Quarantine,
                    analysis.Id,
                    selectedFileIds,
                    "scheduled-quarantine:" + analysis.Id.ToString(CultureInfo.InvariantCulture),
                    cancellationToken).ConfigureAwait(false);
                if (scheduled.Status != OperationRequestStatus.Pending)
                {
                    string outcome = scheduled.Status == OperationRequestStatus.Running
                        ? "request-active"
                        : "request-already-processed";
                    return new FontTaskExecutionResult(analysis.Id, outcome);
                }

                request = await _repository.ClaimNextOperationAsync(
                    FontOperationKind.Quarantine,
                    cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    return new FontTaskExecutionResult(analysis.Id, "request-active");
                }
            }

            if (!await IsCurrentRequestAnalysisAsync(request, cancellationToken).ConfigureAwait(false))
            {
                const string error = "The request analysis is missing, stale, or no longer matches the published inventory.";
                await CompleteRequestAsync(request, OperationRequestStatus.Failed, error).ConfigureAwait(false);
                var exception = new InvalidOperationException(error);
                AddOperationRequestIdentifiers(exception, request);
                throw exception;
            }

            progress.Report(5);
            return await ExecuteRequestAsync(
                request,
                (operationRequest, token) => _quarantine(operationRequest, new ScaledProgress(progress, 5, 95), token),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseTask();
        }
    }

    /// <summary>Consumes at most one explicit restore request.</summary>
    /// <param name="cancellationToken">A token that cancels before or during restore.</param>
    /// <returns>The maintenance batch or safe-skip summary.</returns>
    public async Task<FontTaskExecutionResult> RestoreQuarantinedFontsAsync(CancellationToken cancellationToken)
    {
        if (!TryEnterTask(cancellationToken))
        {
            return new FontTaskExecutionResult(null, "task-active");
        }

        try
        {
            OperationRequest? request = await _repository.ClaimNextOperationAsync(
                FontOperationKind.Restore,
                cancellationToken).ConfigureAwait(false);
            return request is null
                ? new FontTaskExecutionResult(null, "request-missing")
                : await ExecuteRequestAsync(request, _restore, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseTask();
        }
    }

    /// <summary>Purges quarantine items only through the manager's fixed thirty-day verification policy.</summary>
    /// <param name="progress">Receives verification and deletion progress.</param>
    /// <param name="cancellationToken">A token that cancels before the next purge item.</param>
    /// <returns>The compact purge summary.</returns>
    public Task<FontTaskExecutionResult> PurgeExpiredQuarantineAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => RunPurgeAsync(progress, allowScheduledWork: true, cancellationToken);

    private async Task<FontTaskExecutionResult> RunPurgeAsync(IProgress<double> progress, bool allowScheduledWork, CancellationToken cancellationToken)
    {
        if (!TryEnterTask(cancellationToken))
        {
            return new FontTaskExecutionResult(null, "task-active");
        }

        try
        {
            OperationRequest? request = allowScheduledWork
                ? await ClaimOrSynthesizeTaskRequestAsync(FontOperationKind.Purge, cancellationToken).ConfigureAwait(false)
                : await _repository.ClaimNextOperationAsync(FontOperationKind.Purge, cancellationToken).ConfigureAwait(false);
            if (request is null)
            {
                return new FontTaskExecutionResult(null, "request-missing");
            }

            try
            {
                _logger.LogInformation("[AssSubsetter] Quarantine purge verification started. RequestId: {RequestId}.", request.Id);
                progress.Report(5);
                int purged = await _purge(new ScaledProgress(progress, 5, 95), cancellationToken).ConfigureAwait(false);
                progress.Report(95);
                await CompleteRequestAsync(request, OperationRequestStatus.Complete, null).ConfigureAwait(false);
                return new FontTaskExecutionResult(
                    null,
                    "purged:" + purged.ToString(CultureInfo.InvariantCulture),
                    request.Id);
            }
            catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
            {
                await CompleteRequestAsync(request, OperationRequestStatus.Cancelled, "The scheduled task was cancelled.").ConfigureAwait(false);
                AddOperationRequestIdentifiers(exception, request);
                throw;
            }
            catch (Exception exception)
            {
                await CompleteRequestAsync(request, OperationRequestStatus.Failed, exception.Message).ConfigureAwait(false);
                AddOperationRequestIdentifiers(exception, request);
                throw;
            }
        }
        finally
        {
            ReleaseTask();
        }
    }

    /// <summary>Executes one explicit filename request or creates one from current persisted candidates.</summary>
    /// <param name="progress">Receives file and finalization progress.</param>
    /// <param name="cancellationToken">A token that cancels before or during normalization.</param>
    /// <returns>The maintenance batch or safe-skip summary.</returns>
    public Task<FontTaskExecutionResult> NormalizeFilenamesAsync(IProgress<double> progress, CancellationToken cancellationToken)
        => RunFilenameOperationAsync(progress, allowScheduledWork: true, cancellationToken);

    private async Task<FontTaskExecutionResult> RunFilenameOperationAsync(IProgress<double> progress, bool allowScheduledWork, CancellationToken cancellationToken)
    {
        if (!TryEnterTask(cancellationToken))
        {
            return new FontTaskExecutionResult(null, "task-active");
        }

        try
        {
            OperationRequest? request = await _repository.ClaimNextOperationAsync(
                FontOperationKind.NormalizeNames,
                cancellationToken).ConfigureAwait(false);
            if (request is not null)
            {
                return await ExecuteRequestAsync(request, (operation, token) => _filenameNormalizer.ExecuteAsync(operation, token, progress), cancellationToken).ConfigureAwait(false);
            }

            request = await _repository.ClaimNextOperationAsync(FontOperationKind.RollbackNames, cancellationToken).ConfigureAwait(false);
            if (request is not null)
            {
                return await ExecuteRequestAsync(request, (operation, token) => _filenameNormalizer.RollbackAsync(operation, token, progress), cancellationToken).ConfigureAwait(false);
            }

            if (!allowScheduledWork)
            {
                return new FontTaskExecutionResult(null, "request-missing");
            }

            OperationRequest? created = await _filenameNormalizer.CreateCurrentRequestAsync(cancellationToken).ConfigureAwait(false);
            if (created is null)
            {
                return new FontTaskExecutionResult(null, "rename-candidates-empty");
            }

            request = await _repository.ClaimNextOperationAsync(
                FontOperationKind.NormalizeNames,
                cancellationToken).ConfigureAwait(false);
            return request is null
                ? new FontTaskExecutionResult(null, "request-active", created.Id)
                : await ExecuteRequestAsync(request, (operation, token) => _filenameNormalizer.ExecuteAsync(operation, token, progress), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ReleaseTask();
        }
    }

    private bool TryEnterTask(CancellationToken cancellationToken)
    {
        lock (_taskLifetime)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_taskActive)
            {
                return false;
            }

            _taskActive = true;
            return true;
        }
    }

    private void ReleaseTask()
    {
        lock (_taskLifetime)
        {
            _taskActive = false;
        }

        _signalPendingRequests?.Invoke();
    }

    private async Task<OperationRequest> ClaimOrSynthesizeTaskRequestAsync(
        FontOperationKind kind,
        CancellationToken cancellationToken)
    {
        OperationRequest? request = await _repository.ClaimNextOperationAsync(kind, cancellationToken).ConfigureAwait(false);
        if (request is not null)
        {
            return request;
        }

        await _requestStore.CreateTaskAsync(
            kind,
            "scheduled-" + kind.ToString().ToLowerInvariant() + ":" + Guid.NewGuid().ToString("N"),
            cancellationToken).ConfigureAwait(false);
        request = await _repository.ClaimNextOperationAsync(kind, cancellationToken).ConfigureAwait(false);
        return request ?? throw new InvalidOperationException("The scheduled operation request could not be claimed.");
    }

    private async Task<OperationRequest?> ClaimAnalysisOrRebuildRequestAsync(bool allowScheduledWork, CancellationToken cancellationToken)
    {
        OperationRequest? request = await _repository.ClaimNextOperationAsync(FontOperationKind.Analyze, cancellationToken).ConfigureAwait(false);
        if (request is not null)
        {
            return request;
        }

        request = await _repository.ClaimNextOperationAsync(FontOperationKind.RebuildDerivedData, cancellationToken).ConfigureAwait(false);
        return request ?? (allowScheduledWork
            ? await ClaimOrSynthesizeTaskRequestAsync(FontOperationKind.Analyze, cancellationToken).ConfigureAwait(false)
            : null);
    }

    private static bool IsCurrent(AnalysisBatch analysis, IReadOnlyList<FontFileRecord> inventory)
        => analysis.Status == AnalysisBatchStatus.Complete
            && inventory.Count > 0
            && inventory.All(file => file.GenerationId == analysis.GenerationId);

    private async Task<bool> IsCurrentRequestAnalysisAsync(OperationRequest request, CancellationToken cancellationToken)
    {
        if (!request.AnalysisBatchId.HasValue)
        {
            return false;
        }

        AnalysisBatch? analysis = await _repository.GetAnalysisBatchAsync(
            request.AnalysisBatchId.Value,
            cancellationToken).ConfigureAwait(false);
        if (analysis is null)
        {
            return false;
        }

        IReadOnlyList<FontFileRecord> inventory = await _repository.LoadPublishedInventoryAsync(cancellationToken).ConfigureAwait(false);
        return IsCurrent(analysis, inventory);
    }

    private async Task<FontTaskExecutionResult> ExecuteRequestAsync(
        OperationRequest request,
        Func<OperationRequest, CancellationToken, Task<MaintenanceBatch>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("[AssSubsetter] File maintenance started. RequestId: {RequestId}; Kind: {Kind}.", request.Id, request.Kind);
            var stopwatch = Stopwatch.StartNew();
            MaintenanceBatch batch = await operation(request, cancellationToken).ConfigureAwait(false);
            string counts = batch.RenameItems.Count > 0
                ? string.Join(", ", batch.RenameItems.GroupBy(item => item.Status).Select(group => group.Key + ":" + group.Count()))
                : string.Join(", ", batch.Items.GroupBy(item => item.Status).Select(group => group.Key + ":" + group.Count()));
            string errors = string.Join("; ", batch.RenameItems.Select(item => item.Error).Concat(batch.Items.Select(item => item.Error))
                .Where(error => !string.IsNullOrEmpty(error)).GroupBy(error => error).Select(group => group.Key + " (" + group.Count() + ")"));
            _logger.Log(
                batch.Status == MaintenanceBatchStatus.Complete ? LogLevel.Information : LogLevel.Warning,
                "[AssSubsetter] File maintenance finished. RequestId: {RequestId}; BatchId: {BatchId}; Kind: {Kind}; Status: {Status}; Counts: {Counts}; Reasons: {Reasons}; ElapsedMs: {ElapsedMs}.",
                request.Id,
                batch.Id,
                request.Kind,
                batch.Status,
                counts,
                errors,
                stopwatch.ElapsedMilliseconds);
            OperationRequestStatus requestStatus = batch.Status switch
            {
                MaintenanceBatchStatus.Cancelled => OperationRequestStatus.Cancelled,
                MaintenanceBatchStatus.Failed or MaintenanceBatchStatus.Running => OperationRequestStatus.Failed,
                _ => OperationRequestStatus.Complete
            };
            await CompleteRequestAsync(
                request,
                requestStatus,
                requestStatus == OperationRequestStatus.Failed
                    ? batch.Status == MaintenanceBatchStatus.Running ? "The maintenance batch was interrupted; its durable manifest remains recoverable." : "The maintenance batch failed."
                    : null).ConfigureAwait(false);
            return new FontTaskExecutionResult(batch.Id, batch.Status.ToString(), request.Id);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            await CompleteRequestAsync(request, OperationRequestStatus.Cancelled, "The scheduled task was cancelled.").ConfigureAwait(false);
            AddOperationRequestIdentifiers(exception, request);
            throw;
        }
        catch (Exception exception)
        {
            await CompleteRequestAsync(request, OperationRequestStatus.Failed, exception.Message).ConfigureAwait(false);
            AddOperationRequestIdentifiers(exception, request);
            throw;
        }
    }

    private Task CompleteRequestAsync(OperationRequest request, OperationRequestStatus status, string? error)
    {
        return _repository.UpdateOperationAsync(
            request with
            {
                Status = status,
                CompletedUtc = _timeProvider.GetUtcNow(),
                Error = error
            },
            CancellationToken.None);
    }

    private static void AddOperationRequestIdentifiers(Exception exception, OperationRequest request)
    {
        exception.Data[FontScheduledTaskExecution.OperationRequestIdDataKey] = request.Id;
        long? batchId = request.AnalysisBatchId;
        if (!batchId.HasValue)
        {
            try
            {
                batchId = JsonSerializer.Deserialize<FontOperationRequestPayload>(request.RequestJson, JsonOptions)?.BatchId;
            }
            catch (JsonException)
            {
                batchId = null;
            }
        }

        if (batchId is > 0)
        {
            exception.Data[FontScheduledTaskExecution.BatchIdDataKey] = batchId.Value;
        }
    }

    private void LogStage(OperationRequest request, string stage, Stopwatch stopwatch)
        => _logger.LogInformation(
            "[AssSubsetter] Font maintenance stage. RequestId: {RequestId}; Kind: {Kind}; Stage: {Stage}; ElapsedMs: {ElapsedMs}.",
            request.Id,
            request.Kind,
            stage,
            stopwatch.ElapsedMilliseconds);

    private sealed class ScaledProgress(IProgress<double> target, double minimum, double maximum) : IProgress<double>
    {
        public void Report(double value)
        {
            if (!double.IsFinite(value))
            {
                return;
            }

            double bounded = Math.Clamp(value, 0, 100);
            target.Report(minimum + ((maximum - minimum) * bounded / 100));
        }
    }
}
