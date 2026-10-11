using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <summary>
/// Persistence seam for versioned font inventory data.
/// </summary>
public interface IFontRepository
{
    /// <summary>Loads filename authorizations still pending or running.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Active immutable requests.</returns>
    Task<IReadOnlyList<OperationRequest>> LoadActiveRenameRequestsAsync(CancellationToken cancellationToken);

    /// <summary>Loads all durable filename item outcomes with their owning identities.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Execution outcomes.</returns>
    Task<IReadOnlyList<RenameItemRecord>> LoadRenameStateItemsAsync(CancellationToken cancellationToken);

    /// <summary>Loads filename requests including failures before batch allocation.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Saved filename authorizations.</returns>
    Task<IReadOnlyList<OperationRequest>> LoadRenameStateRequestsAsync(CancellationToken cancellationToken);

    /// <summary>Loads the latest successful analysis operation with its scan outcome.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The latest completed operation, if any.</returns>
    Task<OperationRequest?> GetLatestCompletedAnalysisOperationAsync(CancellationToken cancellationToken);

    /// <summary>Initializes persistent storage.</summary>
    /// <param name="cancellationToken">A token that cancels initialization.</param>
    /// <returns>A task representing initialization.</returns>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>Creates an unpublished inventory generation.</summary>
    /// <param name="input">The complete scan input.</param>
    /// <param name="changeReasons">Reasons for replacing the current publication.</param>
    /// <param name="cancellationToken">A token that cancels generation creation.</param>
    /// <returns>The new generation identifier.</returns>
    Task<long> BeginGenerationAsync(FontAnalysisInputSnapshot input, IReadOnlyList<string> changeReasons, CancellationToken cancellationToken);

    /// <summary>Gets the inputs associated with the current publication.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The published inputs, or null before initialization.</returns>
    Task<FontAnalysisInputSnapshot?> GetPublishedAnalysisInputAsync(CancellationToken cancellationToken);

    /// <summary>Writes inventory records into an unpublished generation.</summary>
    /// <param name="generationId">The unpublished generation identifier.</param>
    /// <param name="files">The physical font records to persist.</param>
    /// <param name="cancellationToken">A token that cancels the write.</param>
    /// <returns>A task representing the write.</returns>
    Task WriteInventoryAsync(long generationId, IReadOnlyList<FontFileRecord> files, CancellationToken cancellationToken);

    /// <summary>Deletes superseded inventory and abandoned staged inventory that have no retained report or rename plan.</summary>
    /// <param name="publishedGenerationRetentionCount">Number of recent published generations to keep; zero retains them all.</param>
    /// <param name="abandonedBeforeUtc">Building generations created before this UTC instant can be removed.</param>
    /// <param name="progress">Reports completed generation deletions from zero through one hundred.</param>
    /// <param name="cancellationToken">Cancels pruning between generations or during a deletion.</param>
    /// <returns>The number of inventory generations removed.</returns>
    Task<int> PruneObsoleteGenerationsAsync(int publishedGenerationRetentionCount, DateTimeOffset abandonedBeforeUtc, IProgress<double>? progress, CancellationToken cancellationToken);

    /// <summary>Loads runtime Faces from the currently published generation.</summary>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The immutable runtime Face snapshot.</returns>
    Task<IReadOnlyList<RuntimeFontFace>> LoadPublishedRuntimeFacesAsync(CancellationToken cancellationToken);

    /// <summary>Loads runtime Faces for a staged or published generation before publication.</summary>
    /// <param name="generationId">The generation to prepare for publication.</param>
    /// <param name="cancellationToken">Cancels the snapshot query.</param>
    /// <returns>The generation's immutable runtime Face snapshot.</returns>
    Task<IReadOnlyList<RuntimeFontFace>> LoadRuntimeFacesAsync(long generationId, CancellationToken cancellationToken);

    /// <summary>Loads file and Face metadata from the currently published generation without large Unicode coverage arrays.</summary>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The published physical-file inventory.</returns>
    Task<IReadOnlyList<FontFileRecord>> LoadPublishedInventoryAsync(CancellationToken cancellationToken);

    /// <summary>Loads complete published file and Face evidence, including Unicode coverage needed for scan reuse.</summary>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The published inventory for a new staged generation.</returns>
    Task<IReadOnlyList<FontFileRecord>> LoadPublishedInventoryForReuseAsync(CancellationToken cancellationToken);

    /// <summary>Loads complete file and Face evidence for one persisted inventory generation.</summary>
    /// <param name="generationId">The persisted generation identifier.</param>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The generation's physical-file inventory.</returns>
    Task<IReadOnlyList<FontFileRecord>> LoadInventoryAsync(long generationId, CancellationToken cancellationToken);

    /// <summary>Checks whether a generation has a durably completed duplicate analysis.</summary>
    /// <param name="generationId">The staged generation to check.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns><see langword="true"/> only when a complete analysis batch exists.</returns>
    Task<bool> HasCompleteAnalysisAsync(long generationId, CancellationToken cancellationToken);

    /// <summary>Loads reusable complete deep evidence for an unchanged physical file.</summary>
    /// <param name="file">The current generation's file identity and inventory fingerprint.</param>
    /// <param name="evidenceKey">The parser and native-environment compatibility key.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The stored payload, or <see langword="null"/> when no compatible evidence exists.</returns>
    Task<string?> LoadReusableDeepEvidenceAsync(FontFileRecord file, string evidenceKey, CancellationToken cancellationToken);

    /// <summary>Persists complete deep evidence for one physical file in the current generation.</summary>
    /// <param name="fileId">The current persisted file identifier.</param>
    /// <param name="evidenceKey">The parser and native-environment compatibility key.</param>
    /// <param name="payload">The complete serialized Face evidence.</param>
    /// <param name="cancellationToken">Cancels persistence.</param>
    /// <returns>A task representing persistence.</returns>
    Task SaveDeepEvidenceAsync(long fileId, string evidenceKey, string payload, CancellationToken cancellationToken);

    /// <summary>Returns the newest completed duplicate-analysis report.</summary>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The newest complete report, or <see langword="null"/>.</returns>
    Task<AnalysisBatch?> GetLatestCompleteAnalysisAsync(CancellationToken cancellationToken);

    /// <summary>Loads one immutable duplicate-analysis report by identifier.</summary>
    /// <param name="batchId">The persisted analysis batch identifier.</param>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The report, or <see langword="null"/> when it does not exist.</returns>
    Task<AnalysisBatch?> GetAnalysisBatchAsync(long batchId, CancellationToken cancellationToken);

    /// <summary>Loads one deterministic administrator page of immutable analysis reports.</summary>
    /// <param name="offset">The zero-based row offset.</param>
    /// <param name="pageSize">The bounded number of reports to load.</param>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The page and exact total matching report count.</returns>
    Task<FontRepositoryPage<AnalysisBatch>> LoadAnalysisBatchPageAsync(long offset, int pageSize, CancellationToken cancellationToken);

    /// <summary>Persists an immutable analysis report and its terminal relationships.</summary>
    /// <param name="batch">The report to persist.</param>
    /// <param name="cancellationToken">A token that cancels the write.</param>
    /// <returns>The persisted report with its generated identifier.</returns>
    /// <param name="preparedCandidates">The complete prepared candidates bound in the same publication transaction.</param>
    Task<AnalysisBatch> PublishAnalysisAsync(AnalysisBatch batch, CancellationToken cancellationToken, FontRenameCandidatePreparation? preparedCandidates = null);

    /// <summary>Marks completed reports for one inventory generation stale.</summary>
    /// <param name="generationId">The superseded inventory generation identifier.</param>
    /// <param name="cancellationToken">A token that cancels the update.</param>
    /// <returns>A task representing the guarded status update.</returns>
    Task MarkAnalysisStaleAsync(long generationId, CancellationToken cancellationToken);

    /// <summary>Retains the newest completed analysis batches and batches required by active work.</summary>
    /// <param name="retentionCount">Number of newest results to retain; zero retains all results.</param>
    /// <param name="cancellationToken">A token that cancels pruning.</param>
    /// <returns>The number of reports deleted.</returns>
    Task<int> PruneAnalysisBatchesAsync(int retentionCount, CancellationToken cancellationToken);

    /// <summary>Returns interrupted candidate calculations to the durable pending queue during host startup.</summary>
    /// <param name="cancellationToken">Cancels recovery.</param>
    /// <returns>A task representing recovery.</returns>
    Task RecoverRenameCandidateRefreshAsync(CancellationToken cancellationToken);

    /// <summary>Atomically accepts a refresh request and protects its calculation identity.</summary>
    /// <param name="request">The proposed immutable request.</param>
    /// <param name="rules">Exact desired naming rules.</param>
    /// <param name="requirements">Current compatible analysis inputs.</param>
    /// <param name="onlyIfNeeded">Avoids automatic retries of terminal contexts.</param>
    /// <param name="cancellationToken">Cancels the transaction.</param>
    /// <returns>The accepted request, or null when automatic refresh is unnecessary.</returns>
    Task<OperationRequest?> CreateRenameCandidateRefreshAsync(OperationRequest request, FontNamingRules rules, FontRenameCandidateReadRequirements requirements, bool onlyIfNeeded, CancellationToken cancellationToken);

    /// <summary>Claims all currently pending requests sharing the oldest calculation root.</summary>
    /// <param name="cancellationToken">Cancels the transaction.</param>
    /// <returns>Original request identities in claim order.</returns>
    Task<IReadOnlyList<OperationRequest>> ClaimRenameCandidateRefreshGroupAsync(CancellationToken cancellationToken);

    /// <summary>Creates an immutable idempotent operation request.</summary>
    /// <param name="request">The validated request.</param>
    /// <param name="cancellationToken">A token that cancels the write.</param>
    /// <returns>The new request, or the existing request for the same idempotency key.</returns>
    Task<OperationRequest> CreateOperationRequestAsync(OperationRequest request, CancellationToken cancellationToken);

    /// <summary>Loads the distinct operation kinds with durable pending authorization.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The operation kinds that still need processing.</returns>
    Task<IReadOnlyList<FontOperationKind>> LoadPendingOperationKindsAsync(CancellationToken cancellationToken);

    /// <summary>Atomically claims the oldest pending request of a given kind.</summary>
    /// <param name="kind">The operation kind to claim.</param>
    /// <param name="cancellationToken">A token that cancels the claim.</param>
    /// <returns>The running request, or <see langword="null"/> when none is pending.</returns>
    Task<OperationRequest?> ClaimNextOperationAsync(FontOperationKind kind, CancellationToken cancellationToken);

    /// <summary>Persists the lifecycle state of an existing operation request.</summary>
    /// <param name="request">The persisted request with its next state.</param>
    /// <param name="cancellationToken">A token that cancels the update.</param>
    /// <returns>A task representing the update.</returns>
    Task UpdateOperationAsync(OperationRequest request, CancellationToken cancellationToken);

    /// <summary>Loads one immutable operation request by identifier.</summary>
    /// <param name="requestId">The persisted request identifier.</param>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The request, or <see langword="null"/> when it does not exist.</returns>
    Task<OperationRequest?> GetOperationRequestAsync(long requestId, CancellationToken cancellationToken);

    /// <summary>Loads an immutable operation request by its unique idempotency key.</summary>
    /// <param name="idempotencyKey">The exact client retry key.</param>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The request, or <see langword="null"/> when the key is unknown.</returns>
    Task<OperationRequest?> GetOperationRequestByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Persists a new durable maintenance batch.</summary>
    /// <param name="batch">The batch to persist.</param>
    /// <param name="cancellationToken">A token that cancels the write.</param>
    /// <returns>The persisted batch with its generated identifier.</returns>
    Task<MaintenanceBatch> SaveMaintenanceBatchAsync(MaintenanceBatch batch, CancellationToken cancellationToken);

    /// <summary>Updates a durable maintenance batch and its per-file states.</summary>
    /// <param name="batch">The persisted batch carrying its next state.</param>
    /// <param name="cancellationToken">A token that cancels the write.</param>
    /// <returns>A task representing the transactional update.</returns>
    Task UpdateMaintenanceBatchAsync(MaintenanceBatch batch, CancellationToken cancellationToken);

    /// <summary>Imports a manifest-backed maintenance batch after derived database replacement.</summary>
    /// <param name="batch">The durable batch reconstructed from its immutable manifest.</param>
    /// <param name="analysisBatchId">The immutable analysis identity stored in the manifest.</param>
    /// <param name="idempotencyKey">The immutable operation idempotency identity.</param>
    /// <param name="requestJson">The path-free immutable operation payload.</param>
    /// <param name="cancellationToken">A token that cancels the import.</param>
    /// <returns>A task representing idempotent import.</returns>
    Task ImportMaintenanceBatchAsync(
        MaintenanceBatch batch,
        long analysisBatchId,
        string idempotencyKey,
        string requestJson,
        CancellationToken cancellationToken);

    /// <summary>Loads a durable maintenance batch.</summary>
    /// <param name="batchId">The persisted batch identifier.</param>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The batch, or <see langword="null"/> when it does not exist.</returns>
    Task<MaintenanceBatch?> GetMaintenanceBatchAsync(long batchId, CancellationToken cancellationToken);

    /// <summary>Loads the durable maintenance batch assigned to an immutable operation request.</summary>
    /// <param name="operationRequestId">The immutable operation request identifier.</param>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The batch, or <see langword="null"/> when the request has not started one.</returns>
    Task<MaintenanceBatch?> GetMaintenanceBatchByOperationRequestAsync(long operationRequestId, CancellationToken cancellationToken);

    /// <summary>Loads one deterministic administrator page of durable maintenance batches.</summary>
    /// <param name="offset">The zero-based row offset.</param>
    /// <param name="pageSize">The bounded number of batches to load.</param>
    /// <param name="kinds">The optional fixed set of persisted operation kinds to include.</param>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The page and exact total matching batch count.</returns>
    Task<FontRepositoryPage<MaintenanceBatch>> LoadMaintenanceBatchPageAsync(long offset, int pageSize, IReadOnlySet<FontOperationKind>? kinds, CancellationToken cancellationToken);

    /// <summary>Loads the latest quarantine state per source path across operation batches.</summary>
    /// <param name="offset">The zero-based row offset.</param>
    /// <param name="pageSize">The bounded page size.</param>
    /// <param name="statuses">The item states included in this view.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The matching items and exact total count.</returns>
    Task<FontRepositoryPage<QuarantineItemRecord>> LoadQuarantineItemPageAsync(long offset, int pageSize, IReadOnlySet<QuarantineItemStatus> statuses, CancellationToken cancellationToken);

    /// <summary>Returns an administrator-facing database health snapshot.</summary>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The current database health snapshot.</returns>
    Task<FontDatabaseHealth> GetDatabaseHealthAsync(CancellationToken cancellationToken);

    /// <summary>Persists one local inventory or analysis performance sample.</summary>
    /// <param name="sample">The measured phase sample.</param>
    /// <param name="cancellationToken">A token that cancels persistence.</param>
    /// <returns>A task representing the write.</returns>
    Task SavePerformanceSampleAsync(PerformanceSample sample, CancellationToken cancellationToken);

    /// <summary>Loads the newest local performance samples.</summary>
    /// <param name="maximumCount">The maximum number of samples to return.</param>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The samples ordered newest first.</returns>
    Task<IReadOnlyList<PerformanceSample>> LoadRecentPerformanceSamplesAsync(int maximumCount, CancellationToken cancellationToken);

    /// <summary>Reads lightweight publication and candidate metadata.</summary>
    /// <param name="requirements">The current compatible inputs.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The stored context and its validity.</returns>
    Task<FontRenameCandidateReadContext> GetRenameCandidateContextAsync(FontRenameCandidateReadRequirements requirements, CancellationToken cancellationToken);

    /// <summary>Reads selected persisted candidates in one snapshot; null is a background full read.</summary>
    /// <param name="fileIds">Exact selected IDs, or null for a background read.</param>
    /// <param name="requirements">The current compatible inputs.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The matching candidates and context.</returns>
    Task<FontRenameCandidateSelection> LoadRenameCandidatesAsync(IReadOnlyList<long>? fileIds, FontRenameCandidateReadRequirements requirements, CancellationToken cancellationToken);

    /// <summary>Updates candidate generation metadata for the current publication.</summary>
    /// <param name="state">The desired state.</param>
    /// <param name="requirements">The compatible inputs.</param>
    /// <param name="cancellationToken">Cancels the transaction.</param>
    /// <returns>The completed write.</returns>
    Task SetRenameCandidateStateAsync(FontRenameCandidateState state, FontRenameCandidateReadRequirements requirements, CancellationToken cancellationToken);

    /// <summary>Atomically replaces candidates after validating publication and request identity.</summary>
    /// <param name="preparation">The complete prepared result.</param>
    /// <param name="requirements">The compatible inputs.</param>
    /// <param name="expectedRequestId">The calculation request whose result is accepted.</param>
    /// <param name="cancellationToken">Cancels and rolls back the write.</param>
    /// <returns>The completed write.</returns>
    Task SaveRenameCandidatesAsync(FontRenameCandidatePreparation preparation, FontRenameCandidateReadRequirements requirements, long? expectedRequestId, CancellationToken cancellationToken);

    /// <summary>Reads one status page, count and authorization context under the same snapshot.</summary>
    /// <param name="status">The status category.</param>
    /// <param name="page">The one-based page.</param>
    /// <param name="pageSize">The bounded page size.</param>
    /// <param name="requirements">Current compatible inputs.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Only the requested rows and consistent metadata.</returns>
    Task<FontRenameStatePage> LoadRenameStatePageAsync(string status, int page, int pageSize, FontRenameCandidateReadRequirements requirements, CancellationToken cancellationToken);
}
