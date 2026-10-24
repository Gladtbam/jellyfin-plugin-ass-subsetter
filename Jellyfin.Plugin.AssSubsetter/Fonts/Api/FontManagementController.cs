using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance.Naming;
using Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Api;

/// <summary>Provides elevated administrator routes for persisted font inventory analysis and durable maintenance requests.</summary>
[ApiController]
[Route("AssSubsetter/Fonts")]
[Authorize(Policy = "RequiresElevation")]
public sealed class FontManagementController : ControllerBase
{
    private readonly FontManagementQuery _query;
    private readonly FontOperationRequestStore _requestStore;
    private readonly FontRenameCandidateService _renameCandidates;
    private readonly FontRenameCandidateRefreshQueue _candidateRefreshQueue;
    private readonly IFontManagementReader _repository;
    private readonly IFontOperationRequestDispatcher _operationDispatcher;
    private readonly IFontRestoreRequestDispatcher _restoreDispatcher;

    /// <summary>Initializes a new instance of the <see cref="FontManagementController"/> class.</summary>
    /// <param name="query">The persisted safe read-model query service.</param>
    /// <param name="requestStore">The durable immutable operation-request store.</param>
    /// <param name="repository">The repository used for reference validation and export.</param>
    /// <param name="operationDispatcher">Wakes pending operation scheduling only after persistence.</param>
    /// <param name="restoreDispatcher">Wakes plugin-owned restore processing after persistence.</param>
    /// <param name="candidateRefreshQueue">The durable background candidate refresh queue.</param>
    /// <param name="renameCandidates">The immutable preview and current-rule validation service.</param>
    public FontManagementController(FontManagementQuery query, FontOperationRequestStore requestStore, IFontManagementReader repository, IFontOperationRequestDispatcher operationDispatcher, IFontRestoreRequestDispatcher restoreDispatcher, FontRenameCandidateService renameCandidates, FontRenameCandidateRefreshQueue candidateRefreshQueue)
    {
        _query = query ?? throw new ArgumentNullException(nameof(query));
        _requestStore = requestStore ?? throw new ArgumentNullException(nameof(requestStore));
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _operationDispatcher = operationDispatcher ?? throw new ArgumentNullException(nameof(operationDispatcher));
        _restoreDispatcher = restoreDispatcher ?? throw new ArgumentNullException(nameof(restoreDispatcher));
        _renameCandidates = renameCandidates ?? throw new ArgumentNullException(nameof(renameCandidates));
        _candidateRefreshQueue = candidateRefreshQueue;
    }

    /// <summary>Gets the persisted font inventory overview.</summary>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The current overview.</returns>
    [HttpGet("overview")]
    public Task<FontOverviewDto> GetOverviewAsync(CancellationToken cancellationToken) => _query.GetOverviewAsync(cancellationToken);

    /// <summary>Gets pageable persisted duplicate-analysis reports.</summary>
    /// <param name="page">The requested one-based page.</param>
    /// <param name="pageSize">The requested page size.</param>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The bounded analysis-report page.</returns>
    [HttpGet("analysis-batches")]
    public Task<PagedResult<AnalysisBatchDto>> GetAnalysisBatchesAsync(int page = 1, int pageSize = 50, CancellationToken cancellationToken = default) => _query.GetAnalysisBatchesAsync(page, pageSize, cancellationToken);

    /// <summary>Gets pageable duplicate groups from a persisted analysis report.</summary>
    /// <param name="page">The requested one-based page.</param>
    /// <param name="pageSize">The requested page size.</param>
    /// <param name="analysisBatchId">The optional source analysis report.</param>
    /// <param name="decision">The allowlisted member decision filter.</param>
    /// <param name="sort">The allowlisted group sort field.</param>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The bounded group page.</returns>
    [HttpGet("groups")]
    public async Task<ActionResult<PagedResult<FontGroupDto>>> GetGroupsAsync(int page = 1, int pageSize = 50, long? analysisBatchId = null, string? decision = null, string? sort = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _query.GetGroupsAsync(new FontGroupQuery(page, pageSize, analysisBatchId, decision, sort), cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    /// <summary>Gets pageable persisted relationship and unclassifiable-state report rows.</summary>
    /// <param name="page">The requested one-based page.</param>
    /// <param name="pageSize">The requested page size.</param>
    /// <param name="analysisBatchId">The optional source analysis report.</param>
    /// <param name="relationship">The optional allowlisted relationship name.</param>
    /// <param name="unclassifiable">The optional all, only, or exclude unclassifiable-state filter.</param>
    /// <param name="directory">The optional exact persisted parent directory.</param>
    /// <param name="technology">The optional allowlisted font technology.</param>
    /// <param name="action">The optional allowlisted report action.</param>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The bounded filtered report page.</returns>
    [HttpGet("reports")]
    public async Task<ActionResult<PagedResult<FontReportItemDto>>> GetReportsAsync(
        int page = 1,
        int pageSize = 50,
        long? analysisBatchId = null,
        string? relationship = null,
        string? unclassifiable = null,
        string? directory = null,
        string? technology = null,
        string? action = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await _query.GetReportsAsync(
                new FontReportQuery(page, pageSize, analysisBatchId, relationship, unclassifiable, directory, technology, action),
                cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    /// <summary>Gets structured safe evidence for one persisted duplicate group.</summary>
    /// <param name="analysisBatchId">The owning persisted analysis batch identifier.</param>
    /// <param name="groupId">The persisted group identifier.</param>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The group detail or a not-found response.</returns>
    [HttpGet("analysis-batches/{analysisBatchId:long}/groups/{groupId:long}")]
    public async Task<ActionResult<FontGroupDetailDto>> GetGroupDetailAsync(long analysisBatchId, long groupId, CancellationToken cancellationToken)
    {
        FontGroupDetailDto? detail = await _query.GetGroupDetailAsync(analysisBatchId, groupId, cancellationToken).ConfigureAwait(false);
        return detail is null ? NotFound() : detail;
    }

    /// <summary>Gets pageable durable quarantine and other maintenance batches.</summary>
    /// <param name="page">The requested one-based page.</param>
    /// <param name="pageSize">The requested page size.</param>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The bounded maintenance-batch page.</returns>
    [HttpGet("quarantine/batches")]
    public Task<PagedResult<MaintenanceBatchDto>> GetQuarantineBatchesAsync(int page = 1, int pageSize = 50, CancellationToken cancellationToken = default) => _query.GetMaintenanceBatchesAsync(page, pageSize, new HashSet<FontOperationKind> { FontOperationKind.Quarantine }, cancellationToken);

    /// <summary>Gets the latest active quarantine items across batches by status.</summary>
    /// <param name="page">The requested one-based page.</param>
    /// <param name="pageSize">The requested page size.</param>
    /// <param name="status">The pending, quarantined, or failed status group.</param>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The bounded item page or a validation error.</returns>
    [HttpGet("quarantine/items")]
    public async Task<ActionResult<PagedResult<QuarantineItemDto>>> GetQuarantineItemPageAsync(int page = 1, int pageSize = 50, string status = "quarantined", CancellationToken cancellationToken = default)
    {
        try
        {
            return await _query.GetQuarantineItemsPageAsync(page, pageSize, status, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    /// <summary>Gets durable quarantine item outcomes for one batch.</summary>
    /// <param name="batchId">The persisted maintenance batch identifier.</param>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The item outcomes or a not-found response.</returns>
    [HttpGet("quarantine/batches/{batchId:long}/items")]
    public async Task<ActionResult<IReadOnlyList<QuarantineItemDto>>> GetQuarantineItemsAsync(long batchId, CancellationToken cancellationToken)
    {
        IReadOnlyList<QuarantineItemDto>? items = await _query.GetQuarantineItemsAsync(batchId, cancellationToken).ConfigureAwait(false);
        return items is null ? NotFound() : Ok(items);
    }

    /// <summary>Gets read-only current and historical filename status rows.</summary>
    /// <param name="status">The status view.</param>
    /// <param name="page">One-based page.</param>
    /// <param name="pageSize">Bounded page size.</param>
    /// <param name="cancellationToken">Cancels reads.</param>
    /// <returns>A bounded status page or invalid-filter response.</returns>
    [HttpGet("rename/items")]
    public async Task<ActionResult<RenameStatePageDto>> GetRenameItemsAsync(string status = "pending", int page = 1, int pageSize = 50, CancellationToken cancellationToken = default)
    {
        try
        {
            return Ok(await new FontRenameStateQuery(_repository, _renameCandidates).GetPageAsync(status, page, pageSize, cancellationToken).ConfigureAwait(false));
        }
        catch (ArgumentException exception)
        {
            return BadRequest(new { error = exception.Message });
        }
    }

    /// <summary>Gets original and target paths with durable execution outcomes.</summary>
    /// <param name="batchId">The executed rename or rollback batch.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Execution items or a not-found response.</returns>
    [HttpGet("rename/batches/{batchId:long}/items")]
    public async Task<ActionResult<IReadOnlyList<RenameItemDto>>> GetRenameBatchItemsAsync(long batchId, CancellationToken cancellationToken)
    {
        MaintenanceBatch? batch = await _repository.GetMaintenanceBatchAsync(batchId, cancellationToken).ConfigureAwait(false);
        return batch is null || batch.Kind is not FontOperationKind.NormalizeNames and not FontOperationKind.RollbackNames ? NotFound() : Ok(batch.RenameItems.Select(item => ToRenameItemDto(item.FileId, item.Evidence, item.OldPath, item.NewPath, item.SourceRoot, item.NamingEvidence, item.CollisionFingerprint, item.Status, item.Error)).ToArray());
    }

    private static RenameItemDto ToRenameItemDto(long fileId, FontFileRecord evidence, string oldPath, string? newPath, string? sourceRoot, FontNamingEvidence namingEvidence, string? collisionFingerprint, RenameItemStatus status, string? error)
        => new(fileId, oldPath, newPath, Convert.ToHexString(evidence.Sha256.Span).ToLowerInvariant(), sourceRoot, namingEvidence, collisionFingerprint, status, error) { InspectionError = evidence.Error };

    /// <summary>Gets durable directory outcomes without changing the file results.</summary>
    /// <param name="batchId">The filename execution or rollback batch.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The persisted directory records or not found for other operation kinds.</returns>
    [HttpGet("rename/batches/{batchId:long}/directories")]
    public async Task<ActionResult<IReadOnlyList<RenameDirectoryDto>>> GetRenameDirectoriesAsync(long batchId, CancellationToken cancellationToken)
    {
        MaintenanceBatch? batch = await _repository.GetMaintenanceBatchAsync(batchId, cancellationToken).ConfigureAwait(false);
        if (batch is null || batch.Kind is not FontOperationKind.NormalizeNames and not FontOperationKind.RollbackNames)
        {
            return NotFound();
        }

        using JsonDocument summary = JsonDocument.Parse(batch.SummaryJson);
        IReadOnlyList<RenameDirectoryRecord> records = summary.RootElement.TryGetProperty("Directories", out JsonElement directories)
            ? JsonSerializer.Deserialize<List<RenameDirectoryRecord>>(directories.GetRawText()) ?? []
            : [];
        return Ok(records.Select(record => new RenameDirectoryDto(record.Path, record.Action.ToString(), record.Status.ToString(), record.Error)).ToArray());
    }

    /// <summary>Gets durable filename execution and rollback batches, excluding immutable preview plans.</summary>
    /// <param name="page">The requested one-based page.</param>
    /// <param name="pageSize">The requested page size.</param>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The bounded maintenance-batch page used as the rename-batch shape.</returns>
    [HttpGet("rename/batches")]
    public Task<PagedResult<MaintenanceBatchDto>> GetRenameBatchesAsync(int page = 1, int pageSize = 50, CancellationToken cancellationToken = default) => _query.GetMaintenanceBatchesAsync(page, pageSize, new HashSet<FontOperationKind> { FontOperationKind.NormalizeNames, FontOperationKind.RollbackNames }, cancellationToken);

    /// <summary>Gets safe SQLite health diagnostics.</summary>
    /// <param name="cancellationToken">A token that cancels the diagnostic read.</param>
    /// <returns>The safe database health snapshot.</returns>
    [HttpGet("database-health")]
    public Task<DatabaseHealthDto> GetDatabaseHealthAsync(CancellationToken cancellationToken) => _query.GetDatabaseHealthAsync(cancellationToken);

    /// <summary>Gets bounded persisted performance samples.</summary>
    /// <param name="maximumCount">The requested maximum count.</param>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The bounded samples.</returns>
    [HttpGet("performance-samples")]
    public Task<IReadOnlyList<PerformanceSampleDto>> GetPerformanceSamplesAsync(int maximumCount = 50, CancellationToken cancellationToken = default) => _query.GetPerformanceSamplesAsync(maximumCount, cancellationToken);

    /// <summary>Gets a safe persisted operation lifecycle status.</summary>
    /// <param name="requestId">The persisted request identifier.</param>
    /// <param name="cancellationToken">A token that cancels the read.</param>
    /// <returns>The status or a not-found response.</returns>
    [HttpGet("operations/{requestId:long}")]
    public async Task<ActionResult<OperationRequestDto>> GetOperationAsync(long requestId, CancellationToken cancellationToken)
    {
        OperationRequestDto? request = await _query.GetOperationAsync(requestId, cancellationToken).ConfigureAwait(false);
        return request is null ? NotFound() : request;
    }

    /// <summary>Exports one complete persisted analysis report as an on-demand JSON download.</summary>
    /// <param name="analysisBatchId">The persisted analysis batch identifier.</param>
    /// <param name="cancellationToken">A token that cancels the repository read.</param>
    /// <returns>A JSON export containing the complete persisted report, or a not-found response.</returns>
    [HttpGet("export")]
    public async Task<IActionResult> ExportAsync(long analysisBatchId, CancellationToken cancellationToken)
    {
        AnalysisBatch? batch = await _repository.GetAnalysisBatchAsync(analysisBatchId, cancellationToken).ConfigureAwait(false);
        return batch is null
            ? NotFound()
            : File(JsonSerializer.SerializeToUtf8Bytes(batch), "application/json", $"font-analysis-{analysisBatchId}.json");
    }

    /// <summary>Validates templates without saving settings, scanning or changing fonts.</summary>
    /// <param name="rules">The proposed filename and folder expressions.</param>
    /// <returns>Positioned errors and the canonical default expression.</returns>
    [HttpPost("rename/templates/validate")]
    public FontNamingValidationDto ValidateNamingRules([FromBody] FontNamingRules rules)
        => new(FontNamingTemplate.ValidateRules(rules), FontNamingRules.DefaultFileNameTemplate, FontNamingRules.CurrentFormatVersion, FontNamingFieldCatalog.Fields);

    /// <summary>Persists and queues one refresh-and-analysis request.</summary>
    /// <param name="request">The idempotency-only request body.</param>
    /// <param name="cancellationToken">A token that cancels persistence before queuing.</param>
    /// <returns>The persisted request for polling.</returns>
    [HttpPost("operations/analyze")]
    public Task<ActionResult<OperationRequestDto>> QueueAnalyzeAsync([FromBody] QueueTaskRequest request, CancellationToken cancellationToken) => QueueTaskAsync(FontOperationKind.Analyze, request, cancellationToken);

    /// <summary>Accepts an explicit background recalculation of current rename candidates.</summary>
    /// <param name="request">The original retry key.</param>
    /// <param name="cancellationToken">Cancels acceptance before persistence.</param>
    /// <returns>The accepted durable request.</returns>
    [HttpPost("operations/refresh-rename-candidates")]
    public async Task<ActionResult<OperationRequestDto>> QueueRefreshRenameCandidatesAsync([FromBody] QueueTaskRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var operation = await _candidateRefreshQueue.QueueAsync(request.IdempotencyKey, cancellationToken).ConfigureAwait(false);
            _operationDispatcher.Signal();
            return Accepted(FontManagementQuery.ToDto(operation));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Conflict(new { error = exception.Message });
        }
    }

    /// <summary>Persists and queues one explicitly selected quarantine request.</summary>
    /// <param name="request">The persisted analysis batch and selected physical-file identifiers.</param>
    /// <param name="cancellationToken">A token that cancels persistence before queuing.</param>
    /// <returns>The persisted request for polling or a conflict response.</returns>
    [HttpPost("operations/quarantine")]
    public async Task<ActionResult<OperationRequestDto>> QueueSelectedQuarantineAsync([FromBody] QueueSelectedQuarantineRequest request, CancellationToken cancellationToken)
    {
        if (request.SelectedFileIds is null || request.SelectedFileIds.Count == 0)
        {
            return Conflict(new { error = "At least one selected quarantine file identifier is required." });
        }

        try
        {
            OperationRequest? existing = await _requestStore.FindMatchingAsync(FontOperationKind.Quarantine, request.BatchId, request.SelectedFileIds, request.IdempotencyKey, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                _operationDispatcher.Signal();
                return Accepted(FontManagementQuery.ToDto(existing));
            }
        }
        catch (ArgumentException exception)
        {
            return Conflict(new { error = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new { error = exception.Message });
        }

        AnalysisBatch? batch = await GetCurrentAnalysisAsync(request.BatchId, cancellationToken).ConfigureAwait(false);
        if (batch is null || !IsVerifiedSelection(batch, request.SelectedFileIds))
        {
            return Conflict(new { error = "The selected analysis batch or file selection is missing, invalid, or stale." });
        }

        return await PersistAndQueueSelectionAsync(FontOperationKind.Quarantine, request.BatchId, request.SelectedFileIds, request.IdempotencyKey, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Persists and queues an explicitly confirmed request for every verified quarantine candidate.</summary>
    /// <param name="request">The batch identifier, explicit confirmation, and retry key.</param>
    /// <param name="cancellationToken">A token that cancels persistence before queuing.</param>
    /// <returns>The persisted request for polling or a conflict response.</returns>
    [HttpPost("operations/quarantine/all-verified")]
    public async Task<ActionResult<OperationRequestDto>> QueueAllVerifiedQuarantineAsync([FromBody] QueueAllVerifiedQuarantineRequest request, CancellationToken cancellationToken)
    {
        if (!request.ConfirmAllVerified)
        {
            return Conflict(new { error = "All verified quarantine candidates require explicit confirmation." });
        }

        AnalysisBatch? batch = request.BatchId > 0
            ? await _repository.GetAnalysisBatchAsync(request.BatchId, cancellationToken).ConfigureAwait(false)
            : null;
        long[] candidates = batch?.Groups.SelectMany(group => group.Members)
            .Where(member => member.Decision == DuplicateGroupMemberDecision.QuarantineCandidate)
            .Select(member => member.FileId)
            .Distinct()
            .Order()
            .ToArray() ?? [];
        if (batch is null || candidates.Length == 0)
        {
            return Conflict(new { error = "The analysis batch is missing, stale, or has no verified quarantine candidates." });
        }

        try
        {
            OperationRequest? existing = await _requestStore.FindMatchingAsync(FontOperationKind.Quarantine, request.BatchId, candidates, request.IdempotencyKey, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                _operationDispatcher.Signal();
                return Accepted(FontManagementQuery.ToDto(existing));
            }
        }
        catch (ArgumentException exception)
        {
            return Conflict(new { error = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new { error = exception.Message });
        }

        if (!await IsCurrentAnalysisAsync(batch, cancellationToken).ConfigureAwait(false))
        {
            return Conflict(new { error = "The analysis batch is missing, stale, or has no verified quarantine candidates." });
        }

        return await PersistAndQueueSelectionAsync(FontOperationKind.Quarantine, request.BatchId, candidates, request.IdempotencyKey, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Persists and queues selected restore work for a durable maintenance batch.</summary>
    /// <param name="request">The persisted batch and selected file identifiers.</param>
    /// <param name="cancellationToken">A token that cancels persistence before queuing.</param>
    /// <returns>The persisted request for polling or a conflict response.</returns>
    [HttpPost("operations/restore")]
    public Task<ActionResult<OperationRequestDto>> QueueRestoreAsync([FromBody] QueueMaintenanceSelectionRequest request, CancellationToken cancellationToken) => QueueMaintenanceAsync(FontOperationKind.Restore, FontOperationKind.Quarantine, request, () => _restoreDispatcher.Signal(), cancellationToken);

    /// <summary>Persists and queues a purge request.</summary>
    /// <param name="request">The idempotency-only request body.</param>
    /// <param name="cancellationToken">A token that cancels persistence before queuing.</param>
    /// <returns>The persisted request for polling.</returns>
    [HttpPost("operations/purge")]
    public Task<ActionResult<OperationRequestDto>> QueuePurgeAsync([FromBody] QueueTaskRequest request, CancellationToken cancellationToken) => QueueTaskAsync(FontOperationKind.Purge, request, cancellationToken);

    /// <summary>Authorizes selected current filename candidates using server-computed evidence and targets.</summary>
    /// <param name="request">Displayed context and selected physical identifiers.</param>
    /// <param name="cancellationToken">Cancels authorization.</param>
    /// <returns>A durable request or a conflict requiring the page to refresh.</returns>
    [HttpPost("operations/normalize-names")]
    public async Task<ActionResult<OperationRequestDto>> QueueNormalizeAsync([FromBody] QueueRenameSelectionRequest request, CancellationToken cancellationToken)
    {
        try
        {
            OperationRequest operation = await _renameCandidates.CreateRequestAsync(request.GenerationId, request.AnalysisBatchId, request.RuleFingerprint, request.SelectedFileIds, request.IdempotencyKey, cancellationToken).ConfigureAwait(false);
            if (operation.Status == OperationRequestStatus.Pending)
            {
                _operationDispatcher.Signal();
            }

            return Accepted(FontManagementQuery.ToDto(operation));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or JsonException)
        {
            return Conflict(new { error = exception.Message });
        }
    }

    /// <summary>Persists and queues selected completed rename items for safe rollback.</summary>
    /// <param name="request">The persisted rename batch and selected file identifiers.</param>
    /// <param name="cancellationToken">A token that cancels persistence before queuing.</param>
    /// <returns>The persisted request for polling or a conflict response.</returns>
    [HttpPost("operations/rename-rollback")]
    public Task<ActionResult<OperationRequestDto>> QueueRenameRollbackAsync([FromBody] QueueMaintenanceSelectionRequest request, CancellationToken cancellationToken) => QueueMaintenanceAsync(FontOperationKind.RollbackNames, FontOperationKind.NormalizeNames, request, () => _operationDispatcher.Signal(), cancellationToken);

    /// <summary>Persists and queues a derived-data rebuild through the refresh-and-analysis task.</summary>
    /// <param name="request">The idempotency-only request body.</param>
    /// <param name="cancellationToken">A token that cancels persistence before queuing.</param>
    /// <returns>The persisted request for polling.</returns>
    [HttpPost("operations/rebuild-derived-data")]
    public Task<ActionResult<OperationRequestDto>> QueueRebuildDerivedDataAsync([FromBody] QueueTaskRequest request, CancellationToken cancellationToken) => QueueTaskAsync(FontOperationKind.RebuildDerivedData, request, cancellationToken);

    private async Task<ActionResult<OperationRequestDto>> QueueTaskAsync(FontOperationKind kind, QueueTaskRequest request, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return Conflict(new { error = "An idempotency key is required." });
        }

        try
        {
            OperationRequest operation = await _requestStore.CreateTaskAsync(kind, request.IdempotencyKey, cancellationToken).ConfigureAwait(false);
            _operationDispatcher.Signal();
            return Accepted(FontManagementQuery.ToDto(operation));
        }
        catch (ArgumentException exception)
        {
            return Conflict(new { error = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new { error = exception.Message });
        }
    }

    private async Task<ActionResult<OperationRequestDto>> QueueMaintenanceAsync(FontOperationKind kind, FontOperationKind requiredSourceKind, QueueMaintenanceSelectionRequest request, Action queue, CancellationToken cancellationToken)
    {
        if (request.SelectedFileIds is null || request.SelectedFileIds.Count == 0)
        {
            return Conflict(new { error = "At least one selected persisted file identifier is required." });
        }

        try
        {
            OperationRequest? existing = await _requestStore.FindMatchingAsync(kind, request.BatchId, request.SelectedFileIds, request.IdempotencyKey, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (existing.Status == OperationRequestStatus.Pending)
                {
                    queue();
                }

                return Accepted(FontManagementQuery.ToDto(existing));
            }
        }
        catch (ArgumentException exception)
        {
            return Conflict(new { error = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new { error = exception.Message });
        }

        MaintenanceBatch? batch = await _repository.GetMaintenanceBatchAsync(request.BatchId, cancellationToken).ConfigureAwait(false);
        HashSet<long> batchFileIds = batch?.Kind != requiredSourceKind ? [] : kind == FontOperationKind.RollbackNames
            ? batch.RenameItems.Where(item => item.Status == RenameItemStatus.Renamed).Select(item => item.FileId).ToHashSet()
            : batch.Items.Select(item => item.FileId).ToHashSet();

        if (request.SelectedFileIds.Any(fileId => fileId <= 0 || !batchFileIds.Contains(fileId)))
        {
            return Conflict(new { error = "The referenced maintenance or rename batch is missing, invalid, or stale." });
        }

        try
        {
            OperationRequest operation = await _requestStore.CreateAsync(kind, request.BatchId, request.SelectedFileIds, request.IdempotencyKey, cancellationToken).ConfigureAwait(false);
            queue();
            return Accepted(FontManagementQuery.ToDto(operation));
        }
        catch (ArgumentException exception)
        {
            return Conflict(new { error = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new { error = exception.Message });
        }
    }

    private async Task<ActionResult<OperationRequestDto>> PersistAndQueueSelectionAsync(FontOperationKind kind, long batchId, IReadOnlyList<long> fileIds, string idempotencyKey, CancellationToken cancellationToken)
    {
        try
        {
            OperationRequest operation = await _requestStore.CreateAsync(kind, batchId, fileIds, idempotencyKey, cancellationToken).ConfigureAwait(false);
            _operationDispatcher.Signal();
            return Accepted(FontManagementQuery.ToDto(operation));
        }
        catch (ArgumentException exception)
        {
            return Conflict(new { error = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new { error = exception.Message });
        }
    }

    private async Task<AnalysisBatch?> GetCurrentAnalysisAsync(long batchId, CancellationToken cancellationToken)
    {
        if (batchId <= 0)
        {
            return null;
        }

        AnalysisBatch? batch = await _repository.GetAnalysisBatchAsync(batchId, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<FontFileRecord> inventory = await _repository.LoadPublishedInventoryAsync(cancellationToken).ConfigureAwait(false);
        return batch is { Status: AnalysisBatchStatus.Complete }
            && (await _query.GetAnalysisStateAsync(batch, cancellationToken).ConfigureAwait(false)).IsCurrent
            && inventory.Count > 0
            && inventory.All(file => file.GenerationId == batch.GenerationId)
            ? batch
            : null;
    }

    private async Task<bool> IsCurrentAnalysisAsync(AnalysisBatch batch, CancellationToken cancellationToken)
    {
        IReadOnlyList<FontFileRecord> inventory = await _repository.LoadPublishedInventoryAsync(cancellationToken).ConfigureAwait(false);
        return batch.Status == AnalysisBatchStatus.Complete
            && (await _query.GetAnalysisStateAsync(batch, cancellationToken).ConfigureAwait(false)).IsCurrent
            && inventory.Count > 0
            && inventory.All(file => file.GenerationId == batch.GenerationId);
    }

    private static bool IsVerifiedSelection(AnalysisBatch batch, IReadOnlyList<long> selectedFileIds)
    {
        HashSet<long> candidates = batch.Groups.SelectMany(group => group.Members)
            .Where(member => member.Decision == DuplicateGroupMemberDecision.QuarantineCandidate)
            .Select(member => member.FileId)
            .ToHashSet();
        return selectedFileIds.All(fileId => fileId > 0 && candidates.Contains(fileId));
    }
}
