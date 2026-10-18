using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;
using Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;
using Jellyfin.Plugin.AssSubsetter.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;

/// <summary>Runs the inventory analysis and publication protocol without owning task authorization or mutual exclusion.</summary>
public sealed class FontAnalysisWorkflow
{
    private readonly IFontRepository _repository;
    private readonly ILogger _logger;
    private readonly Func<IProgress<double>?, bool, CancellationToken, Task<FontInventoryScanResult>> _refresh;
    private readonly Func<long, IProgress<double>?, CancellationToken, Task<AnalysisBatch>> _analyze;
    private readonly Func<AnalysisBatch, FontRenameCandidatePreparation?, CancellationToken, Task<AnalysisBatch>> _publish;
    private readonly Func<FontInventoryScanResult, IProgress<double>?, CancellationToken, Task> _validate;
    private readonly Func<AnalysisBatch, CancellationToken, Task<FontRenameCandidatePreparation>>? _prepare;
    private readonly Func<long, CancellationToken, Task>? _repairCandidates;

    /// <summary>Initializes a new instance of the <see cref="FontAnalysisWorkflow"/> class.</summary>
    /// <param name="repository">The versioned font catalog.</param>
    /// <param name="fontCacheManager">The inventory and publication owner.</param>
    /// <param name="analyzer">The duplicate analysis engine.</param>
    /// <param name="candidateService">The rename candidate lifecycle.</param>
    /// <param name="logger">The analysis stage logger.</param>
    public FontAnalysisWorkflow(IFontRepository repository, FontCacheManager fontCacheManager, FontDuplicateAnalyzer analyzer, FontRenameCandidateService candidateService, ILogger<FontAnalysisWorkflow> logger)
        : this(
            repository,
            fontCacheManager.StageInventoryAsync,
            analyzer.AnalyzeAsync,
            (batch, prepared, token) => fontCacheManager.PublishAnalyzedGenerationAsync(batch, token, prepared),
            (scan, progress, token) => fontCacheManager.ValidatePublicationInputsAsync(scan, token, progress),
            candidateService.PrepareForPublicationAsync,
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
            logger: logger)
    {
    }

    internal FontAnalysisWorkflow(
        IFontRepository repository,
        Func<IProgress<double>?, bool, CancellationToken, Task<FontInventoryScanResult>> refresh,
        Func<long, IProgress<double>?, CancellationToken, Task<AnalysisBatch>> analyze,
        Func<AnalysisBatch, FontRenameCandidatePreparation?, CancellationToken, Task<AnalysisBatch>> publish,
        Func<FontInventoryScanResult, IProgress<double>?, CancellationToken, Task>? validate = null,
        Func<AnalysisBatch, CancellationToken, Task<FontRenameCandidatePreparation>>? prepare = null,
        Func<long, CancellationToken, Task>? repairCandidates = null,
        ILogger? logger = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        _analyze = analyze ?? throw new ArgumentNullException(nameof(analyze));
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _validate = validate ?? ((_, _, _) => Task.CompletedTask);
        _prepare = prepare;
        _repairCandidates = repairCandidates;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Analyzes or reuses inventory while the caller holds the maintenance execution slot.</summary>
    /// <param name="operationRequestId">The request identity used for candidate ordering and logging.</param>
    /// <param name="forceRebuild">Whether to rebuild derived evidence.</param>
    /// <param name="progress">Receives analysis stage progress.</param>
    /// <param name="cancellationToken">Cancels work before publication.</param>
    /// <returns>The analyzed or reused batch and scan.</returns>
    public async Task<FontAnalysisWorkflowResult> RunAsync(long operationRequestId, bool forceRebuild, IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var stopwatch = Stopwatch.StartNew();
        LogStage(operationRequestId, "inventory-scan", stopwatch);
        FontInventoryScanResult scan = await _refresh(new ScaledProgress(progress, 0, 60), forceRebuild, cancellationToken).ConfigureAwait(false);
        long generationId = scan.GenerationId;
        if (!scan.HasChanges)
        {
            LogStage(operationRequestId, "validate-reused-inputs", stopwatch);
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
                LogStage(operationRequestId, "repair-rename-candidates", stopwatch);
                await _repairCandidates(operationRequestId, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation("[AssSubsetter] Analysis reused. RequestId: {RequestId}; GenerationId: {GenerationId}; BatchId: {BatchId}; ElapsedMs: {ElapsedMs}.", operationRequestId, generationId, current.Id, stopwatch.ElapsedMilliseconds);
            return new FontAnalysisWorkflowResult(current, scan);
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(60);
        LogStage(operationRequestId, "duplicate-analysis", stopwatch);
        AnalysisBatch batch = await _analyze(generationId, new ScaledProgress(progress, 60, 85), cancellationToken).ConfigureAwait(false);
        if (batch.Status == AnalysisBatchStatus.Complete)
        {
            progress.Report(85);
            LogStage(operationRequestId, "prepare-rename-candidates", stopwatch);
            var prepared = _prepare is null ? null : await _prepare(batch, cancellationToken).ConfigureAwait(false);
            progress.Report(90);
            LogStage(operationRequestId, "validate-publication-inputs", stopwatch);
            await _validate(scan, new ScaledProgress(progress, 90, 97), cancellationToken).ConfigureAwait(false);
            progress.Report(97);
            LogStage(operationRequestId, "publish-analysis", stopwatch);
            batch = await _publish(batch, prepared, cancellationToken).ConfigureAwait(false);
            progress.Report(99);
            _logger.LogInformation("[AssSubsetter] Analysis published. RequestId: {RequestId}; GenerationId: {GenerationId}; BatchId: {BatchId}; ChangeReasons: {ChangeReasons}; Relations: {RelationCount}; RenameCandidates: {CandidateCount}; ElapsedMs: {ElapsedMs}.", operationRequestId, generationId, batch.Id, string.Join(",", scan.ChangeReasons), batch.Relations.Count, prepared?.Items.Count ?? 0, stopwatch.ElapsedMilliseconds);
        }

        return new FontAnalysisWorkflowResult(batch, scan);
    }

    private void LogStage(long requestId, string stage, Stopwatch stopwatch)
        => _logger.LogInformation("[AssSubsetter] Font analysis stage. RequestId: {RequestId}; Stage: {Stage}; ElapsedMs: {ElapsedMs}.", requestId, stage, stopwatch.ElapsedMilliseconds);

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
