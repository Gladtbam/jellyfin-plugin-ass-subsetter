using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <summary>
/// SQLite adapter for versioned font inventory data.
/// </summary>
public sealed partial class SqliteFontRepository : IFontRepository
{
    internal const string RuntimeScoreUpdateSql = """
        UPDATE font_face SET retention_score = $score, is_runtime_preferred = $preferred
        WHERE id = $faceId AND file_id = $fileId
          AND EXISTS (SELECT 1 FROM font_file WHERE id = $fileId AND generation_id = $generationId);
        """;

    private readonly FontDatabase _database;

    /// <summary>Initializes a new instance of the <see cref="SqliteFontRepository"/> class.</summary>
    /// <param name="database">The SQLite connection and writer policy owner.</param>
    public SqliteFontRepository(FontDatabase database)
    {
        _database = database;
    }

    /// <summary>Creates the current SQLite catalog transactionally and applies connection policy.</summary>
    /// <param name="cancellationToken">A token that cancels initialization.</param>
    /// <returns>A task representing initialization and any corruption recovery.</returns>
    public Task InitializeAsync(CancellationToken cancellationToken) => _database.InitializeAsync(cancellationToken);

    /// <summary>Writes a complete physical-file and Face batch into an unpublished generation in one transaction.</summary>
    /// <param name="generationId">The building generation identifier.</param>
    /// <param name="files">The immutable physical-file records and nested Faces.</param>
    /// <param name="cancellationToken">A token that rolls back and cancels the write.</param>
    /// <returns>A task representing the transactional write.</returns>
    /// <exception cref="SqliteException">Thrown when a persisted uniqueness or foreign-key constraint is violated.</exception>
    public Task WriteInventoryAsync(long generationId, IReadOnlyList<FontFileRecord> files, CancellationToken cancellationToken)
    {
        return _database.WriteAsync(
            async (connection, token) =>
            {
                SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
                await using var transactionScope = transaction.ConfigureAwait(false);
                await EnsureBuildingGenerationAsync(connection, transaction, generationId, token).ConfigureAwait(false);
                var writer = new SqliteInventoryWriter(connection, transaction);
                await using var writerScope = writer.ConfigureAwait(false);
                await writer.WriteAsync(generationId, files, token).ConfigureAwait(false);

                await transaction.CommitAsync(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    /// <summary>Deletes unused inventory generations one at a time to bound each SQLite transaction.</summary>
    /// <param name="publishedGenerationRetentionCount">Number of recent published generations to keep; zero retains them all.</param>
    /// <param name="abandonedBeforeUtc">Building generations older than this instant are considered abandoned.</param>
    /// <param name="progress">Reports the share of candidate generations completed.</param>
    /// <param name="cancellationToken">Cancels pruning.</param>
    /// <returns>The number of generations removed.</returns>
    public Task<int> PruneObsoleteGenerationsAsync(int publishedGenerationRetentionCount, DateTimeOffset abandonedBeforeUtc, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(publishedGenerationRetentionCount);
        return _database.WriteAsync(
            async (connection, token) =>
            {
                const string candidatesSql = """
                    SELECT g.id
                    FROM scan_generation g
                    WHERE ((g.status = 2 AND $publishedGenerationRetentionCount > 0
                            AND g.id NOT IN (SELECT id FROM scan_generation WHERE status IN (1, 2) ORDER BY id DESC LIMIT $publishedGenerationRetentionCount))
                           OR (g.status = 0 AND g.created_utc < $abandonedBeforeUtc))
                      AND g.id NOT IN (SELECT published_generation_id FROM schema_info WHERE published_generation_id IS NOT NULL)
                      AND NOT EXISTS (SELECT 1 FROM analysis_batch b WHERE b.generation_id = g.id)
                    ORDER BY g.id;
                    """;
                var candidates = new List<long>();
                SqliteCommand select = connection.CreateCommand();
                await using (select.ConfigureAwait(false))
                {
                    select.CommandText = candidatesSql;
                    select.Parameters.AddWithValue("$publishedGenerationRetentionCount", publishedGenerationRetentionCount);
                    select.Parameters.AddWithValue("$abandonedBeforeUtc", abandonedBeforeUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                    SqliteDataReader reader = await select.ExecuteReaderAsync(token).ConfigureAwait(false);
                    await using (reader.ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(token).ConfigureAwait(false))
                        {
                            candidates.Add(reader.GetInt64(0));
                        }
                    }
                }

                int removed = 0;
                int processed = 0;
                progress?.Report(0);
                foreach (long generationId in candidates)
                {
                    token.ThrowIfCancellationRequested();
                    SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
                    await using var transactionScope = transaction.ConfigureAwait(false);
                    SqliteCommand delete = connection.CreateCommand();
                    await using var deleteScope = delete.ConfigureAwait(false);
                    delete.Transaction = transaction;
                    delete.CommandText = """
                        DELETE FROM scan_generation
                        WHERE id = $generationId
                          AND ((status = 2 AND $publishedGenerationRetentionCount > 0
                                AND id NOT IN (SELECT id FROM scan_generation WHERE status IN (1, 2) ORDER BY id DESC LIMIT $publishedGenerationRetentionCount))
                               OR (status = 0 AND created_utc < $abandonedBeforeUtc))
                          AND id NOT IN (SELECT published_generation_id FROM schema_info WHERE published_generation_id IS NOT NULL)
                          AND NOT EXISTS (SELECT 1 FROM analysis_batch WHERE generation_id = $generationId);
                        """;
                    delete.Parameters.AddWithValue("$generationId", generationId);
                    delete.Parameters.AddWithValue("$publishedGenerationRetentionCount", publishedGenerationRetentionCount);
                    delete.Parameters.AddWithValue("$abandonedBeforeUtc", abandonedBeforeUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                    removed += await delete.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    processed++;
                    progress?.Report(processed * 100d / candidates.Count);
                }

                if (candidates.Count == 0)
                {
                    progress?.Report(100);
                }

                return removed;
            },
            cancellationToken);
    }

    /// <summary>Loads the immutable runtime Face snapshot belonging only to the currently published generation.</summary>
    /// <param name="cancellationToken">A token that cancels the snapshot query.</param>
    /// <returns>Runtime Face records ordered by normalized path and Face index.</returns>
    public Task<IReadOnlyList<RuntimeFontFace>> LoadPublishedRuntimeFacesAsync(CancellationToken cancellationToken)
        => LoadRuntimeFacesCoreAsync(null, cancellationToken);

    /// <summary>Loads the immutable runtime Face snapshot for one staged or published generation.</summary>
    /// <param name="generationId">The generation identifier.</param>
    /// <param name="cancellationToken">A token that cancels the snapshot query.</param>
    /// <returns>Runtime Face records ordered by normalized path and Face index.</returns>
    public Task<IReadOnlyList<RuntimeFontFace>> LoadRuntimeFacesAsync(long generationId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generationId);
        return LoadRuntimeFacesCoreAsync(generationId, cancellationToken);
    }

    /// <summary>Stores complete deep evidence after the current physical file is verified.</summary>
    /// <param name="fileId">The current persisted file identifier.</param>
    /// <param name="evidenceKey">The parser and native-environment compatibility key.</param>
    /// <param name="payload">The serialized evidence.</param>
    /// <param name="cancellationToken">Cancels persistence.</param>
    /// <returns>A task representing the database write.</returns>
    public Task SaveDeepEvidenceAsync(long fileId, string evidenceKey, string payload, CancellationToken cancellationToken)
        => _database.WriteAsync(
            async (connection, token) =>
            {
                SqliteCommand command = connection.CreateCommand();
                await using var commandScope = command.ConfigureAwait(false);
                command.CommandText = "INSERT OR REPLACE INTO font_deep_evidence(file_id, evidence_key, payload_gzip) VALUES ($fileId, $evidenceKey, $payload);";
                command.Parameters.AddWithValue("$fileId", fileId);
                command.Parameters.AddWithValue("$evidenceKey", evidenceKey);
                command.Parameters.AddWithValue("$payload", CompressDeepEvidence(payload));
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    private static byte[] CompressDeepEvidence(string payload)
    {
        using var output = new MemoryStream();
        using (var compressor = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            compressor.Write(bytes);
        }

        return output.ToArray();
    }

    private static string DecompressDeepEvidence(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var decompressor = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        byte[] buffer = new byte[81920];
        int count;
        while ((count = decompressor.Read(buffer)) > 0)
        {
            if (output.Length + count > 128L * 1024 * 1024)
            {
                throw new InvalidDataException("Deep font evidence exceeds the maximum cache entry size.");
            }

            output.Write(buffer, 0, count);
        }

        return new UTF8Encoding(false, true).GetString(output.ToArray());
    }

    /// <summary>Persists a new immutable analysis report and all pair relationships in one transaction.</summary>
    /// <param name="batch">The unpersisted report whose identifier must be zero.</param>
    /// <param name="cancellationToken">A token that rolls back and cancels the write.</param>
    /// <returns>The persisted report with its generated identifier.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the input already has a persisted identifier.</exception>
    /// <param name="preparedCandidates">The complete prepared candidate result.</param>
    public Task<AnalysisBatch> PublishAnalysisAsync(AnalysisBatch batch, CancellationToken cancellationToken, FontRenameCandidatePreparation? preparedCandidates = null)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(batch.Id, 0);
        if (batch.Status != AnalysisBatchStatus.Complete)
        {
            throw new InvalidOperationException("Only a completed analysis can be published.");
        }

        ReadFaceScores(batch.SummaryJson, batch.Status);
        return _database.WriteAsync(
            async (connection, token) =>
            {
                SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
                await using var transactionScope = transaction.ConfigureAwait(false);
                SqliteCommand command = connection.CreateCommand();
                await using var commandScope = command.ConfigureAwait(false);
                command.Transaction = transaction;
                command.CommandText = "SELECT status FROM scan_generation WHERE id = $generationId;";
                command.Parameters.AddWithValue("$generationId", batch.GenerationId);
                object? generationStatus = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                if (generationStatus is null || Convert.ToInt32(generationStatus, CultureInfo.InvariantCulture) != 0)
                {
                    throw new InvalidOperationException("Only a building generation can be published.");
                }

                command.Parameters.Clear();
                command.CommandText = """
                    INSERT INTO analysis_batch(generation_id, status, evidence_version, started_utc, completed_utc, candidate_count, comparison_count, reclaimable_bytes, summary_json)
                    VALUES ($generationId, $status, $evidenceVersion, $startedUtc, $completedUtc, $candidateCount, $comparisonCount, $reclaimableBytes, $summaryJson);
                    SELECT last_insert_rowid();
                    """;
                command.Parameters.AddWithValue("$generationId", batch.GenerationId);
                command.Parameters.AddWithValue("$status", (int)batch.Status);
                command.Parameters.AddWithValue("$evidenceVersion", batch.EvidenceVersion);
                command.Parameters.AddWithValue("$startedUtc", FormatDate(batch.StartedUtc));
                command.Parameters.AddWithValue("$completedUtc", FormatNullableDate(batch.CompletedUtc));
                command.Parameters.AddWithValue("$candidateCount", batch.CandidateCount);
                command.Parameters.AddWithValue("$comparisonCount", batch.ComparisonCount);
                command.Parameters.AddWithValue("$reclaimableBytes", batch.ReclaimableBytes);
                command.Parameters.AddWithValue("$summaryJson", batch.SummaryJson);
                object? result = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                long batchId = Convert.ToInt64(result, CultureInfo.InvariantCulture);

                foreach (FontRelationRecord relation in batch.Relations)
                {
                    await InsertRelationAsync(connection, transaction, batchId, relation, token).ConfigureAwait(false);
                }

                foreach (DuplicateGroupRecord group in batch.Groups)
                {
                    await InsertGroupAsync(connection, transaction, batchId, group, token).ConfigureAwait(false);
                }

                SqliteCommand update = connection.CreateCommand();
                await using var updateScope = update.ConfigureAwait(false);
                update.Transaction = transaction;
                update.CommandText = RuntimeScoreUpdateSql;
                update.Parameters.AddWithValue("$score", 0);
                update.Parameters.AddWithValue("$preferred", 0);
                update.Parameters.AddWithValue("$faceId", 0L);
                update.Parameters.AddWithValue("$fileId", 0L);
                update.Parameters.AddWithValue("$generationId", batch.GenerationId);
                foreach (FontFaceAnalysisRecord face in batch.FaceScores)
                {
                    update.Parameters["$score"].Value = face.RetentionScore;
                    update.Parameters["$preferred"].Value = face.IsRuntimePreferred ? 1 : 0;
                    update.Parameters["$faceId"].Value = face.FaceId;
                    update.Parameters["$fileId"].Value = face.FileId;
                    if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                    {
                        throw new InvalidOperationException("Analyzed runtime score does not belong to the requested inventory generation.");
                    }
                }

                if (preparedCandidates is null)
                {
                    command.Parameters.Clear();
                    command.CommandText = "DELETE FROM rename_candidate; DELETE FROM rename_candidate_state;";
                    await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }
                else
                {
                    ValidatePublishedCandidatePreparation(batch, preparedCandidates);
                    var bound = preparedCandidates with { State = preparedCandidates.State with { AnalysisBatchId = batchId } };
                    await ReplaceRenameCandidatesAsync(connection, transaction, bound, token).ConfigureAwait(false);
                }

                await PublishGenerationCoreAsync(connection, transaction, batch.GenerationId, token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return batch with { Id = batchId };
            },
            cancellationToken);
    }

    /// <summary>Retains recent analysis results while preserving active references and the current valid report.</summary>
    /// <param name="retentionCount">Number of newest results to retain; zero disables pruning.</param>
    /// <param name="cancellationToken">A token that cancels pruning.</param>
    /// <returns>The number of reports deleted.</returns>
    public Task<int> PruneAnalysisBatchesAsync(int retentionCount, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(retentionCount);
        if (retentionCount == 0)
        {
            return Task.FromResult(0);
        }

        return _database.WriteAsync(
            async (connection, token) =>
            {
                SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
                await using var transactionScope = transaction.ConfigureAwait(false);
                SqliteCommand command = connection.CreateCommand();
                await using var commandScope = command.ConfigureAwait(false);
                command.Transaction = transaction;
                command.Parameters.AddWithValue("$complete", (int)AnalysisBatchStatus.Complete);
                command.Parameters.AddWithValue("$stale", (int)AnalysisBatchStatus.Stale);
                command.Parameters.AddWithValue("$failed", (int)AnalysisBatchStatus.Failed);
                command.Parameters.AddWithValue("$retentionCount", retentionCount);
                command.Parameters.AddWithValue("$pending", (int)OperationRequestStatus.Pending);
                command.Parameters.AddWithValue("$running", (int)OperationRequestStatus.Running);
                command.CommandText = """
                    CREATE TEMP TABLE IF NOT EXISTS prune_analysis_batch_ids (id INTEGER PRIMARY KEY);
                    DELETE FROM prune_analysis_batch_ids;
                    INSERT INTO prune_analysis_batch_ids(id)
                    SELECT id FROM analysis_batch
                    WHERE status IN ($complete, $stale, $failed)
                      AND id NOT IN (
                          SELECT id FROM analysis_batch
                          WHERE status IN ($complete, $stale, $failed)
                          ORDER BY COALESCE(completed_utc, started_utc) DESC, id DESC
                          LIMIT $retentionCount
                      )
                      AND id NOT IN (
                          SELECT b.id FROM analysis_batch b
                          JOIN schema_info s ON s.published_generation_id = b.generation_id
                          WHERE b.status = $complete
                          ORDER BY b.completed_utc DESC, b.id DESC
                          LIMIT 1
                      )
                      AND id NOT IN (
                          SELECT analysis_batch_id FROM operation_request
                          WHERE analysis_batch_id IS NOT NULL
                            AND status IN ($pending, $running)
                      );
                    """;
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                // Completed requests retain their audit record, but no longer pin obsolete reports.
                command.CommandText = """
                    UPDATE operation_request SET analysis_batch_id = NULL
                    WHERE analysis_batch_id IN (SELECT id FROM prune_analysis_batch_ids)
                      AND status NOT IN ($pending, $running);
                    """;
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                command.CommandText = "DELETE FROM analysis_batch WHERE id IN (SELECT id FROM prune_analysis_batch_ids);";
                int deleted = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                command.CommandText = "DROP TABLE prune_analysis_batch_ids;";
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return deleted;
            },
            cancellationToken);
    }

    /// <summary>Creates an immutable operation request, returning the existing row when its idempotency key already exists.</summary>
    /// <param name="request">The validated, unpersisted operation request.</param>
    /// <param name="cancellationToken">A token that cancels request persistence.</param>
    /// <returns>The persisted new or pre-existing request.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the input already has a persisted identifier.</exception>
    public Task<OperationRequest> CreateOperationRequestAsync(OperationRequest request, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(request.Id, 0);
        return _database.WriteAsync(
            async (connection, token) =>
            {
                OperationRequest? existing = await FindOperationByIdempotencyKeyAsync(connection, request.IdempotencyKey, token).ConfigureAwait(false);
                if (existing is not null)
                {
                    return existing;
                }

                SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
                await using var transactionScope = transaction.ConfigureAwait(false);

                if (request.Kind == FontOperationKind.NormalizeNames && request.Status is OperationRequestStatus.Pending or OperationRequestStatus.Running)
                {
                    FontRenameExecutionPayload proposed = FontOperationRequestPayloadCodec.ParseRenameExecution(request.RequestJson);
                    await ValidateActiveRenameReservationAsync(connection, transaction, proposed, token).ConfigureAwait(false);
                }

                SqliteCommand command = connection.CreateCommand();
                await using var commandScope = command.ConfigureAwait(false);
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO operation_request(operation_kind, status, analysis_batch_id, idempotency_key, request_json, created_utc, claimed_utc, completed_utc, error, result_json)
                    VALUES ($kind, $status, $analysisBatchId, $idempotencyKey, $requestJson, $createdUtc, $claimedUtc, $completedUtc, $error, $resultJson);
                    SELECT last_insert_rowid();
                    """;
                AddOperationParameters(command, request);
                object? result = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                var persisted = request with { Id = Convert.ToInt64(result, CultureInfo.InvariantCulture) };
                await InsertRenameRequestItemsAsync(connection, transaction, persisted, token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return persisted;
            },
            cancellationToken);
    }

    /// <summary>Atomically changes the oldest pending request of one kind to running inside a serialized write transaction.</summary>
    /// <param name="kind">The operation kind eligible for the claim.</param>
    /// <param name="cancellationToken">A token that cancels the claim transaction.</param>
    /// <returns>The claimed running request, or <see langword="null"/> when no request is pending.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the selected request loses its pending state before the guarded update.</exception>
    public Task<OperationRequest?> ClaimNextOperationAsync(FontOperationKind kind, CancellationToken cancellationToken)
    {
        return _database.WriteAsync(
            async (connection, token) =>
            {
                SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
                await using var transactionScope = transaction.ConfigureAwait(false);
                OperationRequest? request;
                SqliteCommand select = connection.CreateCommand();
                await using (select.ConfigureAwait(false))
                {
                    select.Transaction = transaction;
                    select.CommandText = """
                        SELECT id, operation_kind, status, analysis_batch_id, idempotency_key, request_json,
                               created_utc, claimed_utc, completed_utc, error, result_json
                        FROM operation_request
                        WHERE operation_kind = $kind AND status = $status
                        ORDER BY created_utc, id
                        LIMIT 1;
                        """;
                    select.Parameters.AddWithValue("$kind", (int)kind);
                    select.Parameters.AddWithValue("$status", (int)OperationRequestStatus.Pending);
                    SqliteDataReader reader = await select.ExecuteReaderAsync(token).ConfigureAwait(false);
                    await using var readerScope = reader.ConfigureAwait(false);
                    request = await reader.ReadAsync(token).ConfigureAwait(false) ? ReadOperationRequest(reader) : null;
                }

                if (request is null)
                {
                    await transaction.CommitAsync(token).ConfigureAwait(false);
                    return null;
                }

                DateTimeOffset claimedUtc = DateTimeOffset.UtcNow;
                SqliteCommand update = connection.CreateCommand();
                await using var updateScope = update.ConfigureAwait(false);
                update.Transaction = transaction;
                update.CommandText = "UPDATE operation_request SET status = $running, claimed_utc = $claimedUtc WHERE id = $id AND status = $pending;";
                update.Parameters.AddWithValue("$running", (int)OperationRequestStatus.Running);
                update.Parameters.AddWithValue("$claimedUtc", FormatDate(claimedUtc));
                update.Parameters.AddWithValue("$id", request.Id);
                update.Parameters.AddWithValue("$pending", (int)OperationRequestStatus.Pending);
                int changed = await update.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                if (changed != 1)
                {
                    throw new InvalidOperationException("The pending operation could not be claimed atomically.");
                }

                await transaction.CommitAsync(token).ConfigureAwait(false);
                return request with { Status = OperationRequestStatus.Running, ClaimedUtc = claimedUtc };
            },
            cancellationToken);
    }

    /// <summary>Persists a new durable maintenance batch linked one-to-one with its immutable operation request.</summary>
    /// <param name="batch">The unpersisted maintenance batch.</param>
    /// <param name="cancellationToken">A token that cancels persistence.</param>
    /// <returns>The persisted batch with its generated identifier.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the input already has a persisted identifier.</exception>
    public Task<MaintenanceBatch> SaveMaintenanceBatchAsync(MaintenanceBatch batch, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(batch.Id, 0);
        return _database.WriteAsync(
            async (connection, token) =>
            {
                SqliteCommand command = connection.CreateCommand();
                await using var commandScope = command.ConfigureAwait(false);
                command.CommandText = """
                    INSERT INTO maintenance_batch(operation_request_id, operation_kind, status, started_utc, completed_utc, summary_json)
                    VALUES ($requestId, $kind, $status, $startedUtc, $completedUtc, $summaryJson);
                    SELECT last_insert_rowid();
                    """;
                command.Parameters.AddWithValue("$requestId", batch.OperationRequestId);
                command.Parameters.AddWithValue("$kind", (int)batch.Kind);
                command.Parameters.AddWithValue("$status", (int)batch.Status);
                command.Parameters.AddWithValue("$startedUtc", FormatDate(batch.StartedUtc));
                command.Parameters.AddWithValue("$completedUtc", FormatNullableDate(batch.CompletedUtc));
                command.Parameters.AddWithValue("$summaryJson", batch.SummaryJson);
                object? result = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                return batch with { Id = Convert.ToInt64(result, CultureInfo.InvariantCulture) };
            },
            cancellationToken);
    }

    /// <summary>Updates one durable maintenance batch.</summary>
    /// <param name="batch">The persisted batch carrying its next state.</param>
    /// <param name="cancellationToken">A token that cancels the update.</param>
    /// <returns>A task representing the transactional update.</returns>
    public Task UpdateMaintenanceBatchAsync(MaintenanceBatch batch, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batch.Id);
        return _database.WriteAsync(
            async (connection, token) =>
            {
                SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
                await using var transactionScope = transaction.ConfigureAwait(false);
                SqliteCommand command = connection.CreateCommand();
                await using var commandScope = command.ConfigureAwait(false);
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE maintenance_batch
                    SET status = $status, completed_utc = $completedUtc, summary_json = $summaryJson
                    WHERE id = $batchId;
                    """;
                command.Parameters.AddWithValue("$status", (int)batch.Status);
                command.Parameters.AddWithValue("$completedUtc", FormatNullableDate(batch.CompletedUtc));
                command.Parameters.AddWithValue("$summaryJson", batch.SummaryJson);
                command.Parameters.AddWithValue("$batchId", batch.Id);
                int changed = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                if (changed != 1)
                {
                    throw new InvalidOperationException("The maintenance batch does not exist.");
                }

                await UpsertQuarantineItemsAsync(connection, transaction, batch, token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return 0;
            },
            cancellationToken);
    }

    /// <summary>Imports a manifest-backed maintenance batch after derived database replacement.</summary>
    /// <param name="batch">The durable manifest-backed batch.</param>
    /// <param name="analysisBatchId">The immutable analysis identity stored by the manifest.</param>
    /// <param name="idempotencyKey">The immutable operation idempotency identity.</param>
    /// <param name="requestJson">The path-free immutable operation payload.</param>
    /// <param name="cancellationToken">A token that cancels the import.</param>
    /// <returns>A task representing idempotent import.</returns>
    public Task ImportMaintenanceBatchAsync(
        MaintenanceBatch batch,
        long analysisBatchId,
        string idempotencyKey,
        string requestJson,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batch.Id);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batch.OperationRequestId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(analysisBatchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestJson);
        return _database.WriteAsync(
            async (connection, token) =>
            {
                SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
                await using var transactionScope = transaction.ConfigureAwait(false);
                SqliteCommand requestCommand = connection.CreateCommand();
                await using var requestCommandScope = requestCommand.ConfigureAwait(false);
                requestCommand.Transaction = transaction;
                requestCommand.CommandText = """
                    INSERT OR IGNORE INTO operation_request(
                        id, operation_kind, status, analysis_batch_id, idempotency_key, request_json,
                        created_utc, claimed_utc, completed_utc, error, result_json)
                    VALUES ($id, $kind, $status, NULL, $key, $requestJson, $createdUtc, NULL, $completedUtc, NULL, NULL);
                    """;
                requestCommand.Parameters.AddWithValue("$id", batch.OperationRequestId);
                requestCommand.Parameters.AddWithValue("$kind", (int)batch.Kind);
                requestCommand.Parameters.AddWithValue("$status", (int)OperationRequestStatus.Complete);
                requestCommand.Parameters.AddWithValue("$key", idempotencyKey);
                requestCommand.Parameters.AddWithValue("$requestJson", requestJson);
                requestCommand.Parameters.AddWithValue("$createdUtc", FormatDate(batch.StartedUtc));
                requestCommand.Parameters.AddWithValue("$completedUtc", FormatNullableDate(batch.CompletedUtc));
                await requestCommand.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                SqliteCommand validateRequest = connection.CreateCommand();
                await using (validateRequest.ConfigureAwait(false))
                {
                    validateRequest.Transaction = transaction;
                    validateRequest.CommandText = """
                        SELECT operation_kind, analysis_batch_id, idempotency_key, request_json
                        FROM operation_request
                        WHERE id = $id;
                        """;
                    validateRequest.Parameters.AddWithValue("$id", batch.OperationRequestId);
                    SqliteDataReader reader = await validateRequest.ExecuteReaderAsync(token).ConfigureAwait(false);
                    await using var readerScope = reader.ConfigureAwait(false);
                    if (!await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        throw new InvalidOperationException("The manifest operation identity could not be imported.");
                    }

                    bool analysisIsNull = await reader.IsDBNullAsync(1, token).ConfigureAwait(false);
                    bool matchesSelection = FontOperationRequestPayloadCodec.MatchesSelectionJson(
                        reader.GetString(3),
                        analysisBatchId,
                        batch.Items.Select(item => item.FileId).ToArray());
                    bool matchesOriginal = !analysisIsNull
                        && reader.GetInt64(1) == analysisBatchId
                        && string.Equals(reader.GetString(2), idempotencyKey, StringComparison.Ordinal)
                        && string.Equals(reader.GetString(3), requestJson, StringComparison.Ordinal)
                        && matchesSelection;
                    bool matchesPlaceholder = analysisIsNull
                        && string.Equals(reader.GetString(2), idempotencyKey, StringComparison.Ordinal)
                        && string.Equals(reader.GetString(3), requestJson, StringComparison.Ordinal)
                        && matchesSelection;
                    bool matchesRename = batch.Kind is FontOperationKind.NormalizeNames or FontOperationKind.RollbackNames
                        && (analysisIsNull || reader.GetInt64(1) == analysisBatchId)
                        && string.Equals(reader.GetString(2), idempotencyKey, StringComparison.Ordinal)
                        && string.Equals(reader.GetString(3), requestJson, StringComparison.Ordinal);
                    if ((FontOperationKind)reader.GetInt32(0) != batch.Kind
                        || (!matchesRename && !matchesOriginal && !matchesPlaceholder))
                    {
                        throw new InvalidOperationException("The manifest operation identifier conflicts with an existing immutable request.");
                    }
                }

                await InsertRenameRequestItemsAsync(connection, transaction, new OperationRequest { Id = batch.OperationRequestId, Kind = batch.Kind, RequestJson = requestJson, IdempotencyKey = idempotencyKey }, token).ConfigureAwait(false);

                SqliteCommand batchCommand = connection.CreateCommand();
                await using var batchCommandScope = batchCommand.ConfigureAwait(false);
                batchCommand.Transaction = transaction;
                batchCommand.CommandText = """
                    INSERT OR IGNORE INTO maintenance_batch(
                        id, operation_request_id, operation_kind, status, started_utc, completed_utc, summary_json)
                    VALUES ($id, $requestId, $kind, $status, $startedUtc, $completedUtc, $summaryJson);
                    """;
                batchCommand.Parameters.AddWithValue("$id", batch.Id);
                batchCommand.Parameters.AddWithValue("$requestId", batch.OperationRequestId);
                batchCommand.Parameters.AddWithValue("$kind", (int)batch.Kind);
                batchCommand.Parameters.AddWithValue("$status", (int)batch.Status);
                batchCommand.Parameters.AddWithValue("$startedUtc", FormatDate(batch.StartedUtc));
                batchCommand.Parameters.AddWithValue("$completedUtc", FormatNullableDate(batch.CompletedUtc));
                batchCommand.Parameters.AddWithValue("$summaryJson", batch.SummaryJson);
                await batchCommand.ExecuteNonQueryAsync(token).ConfigureAwait(false);

                SqliteCommand validateBatch = connection.CreateCommand();
                await using (validateBatch.ConfigureAwait(false))
                {
                    validateBatch.Transaction = transaction;
                    validateBatch.CommandText = """
                        SELECT operation_request_id, operation_kind, started_utc
                        FROM maintenance_batch
                        WHERE id = $id;
                        """;
                    validateBatch.Parameters.AddWithValue("$id", batch.Id);
                    SqliteDataReader reader = await validateBatch.ExecuteReaderAsync(token).ConfigureAwait(false);
                    await using var readerScope = reader.ConfigureAwait(false);
                    if (!await reader.ReadAsync(token).ConfigureAwait(false)
                        || reader.GetInt64(0) != batch.OperationRequestId
                        || (FontOperationKind)reader.GetInt32(1) != batch.Kind
                        || ParseDate(reader.GetString(2)) != batch.StartedUtc)
                    {
                        throw new InvalidOperationException("The manifest batch identifier conflicts with an existing immutable batch.");
                    }
                }

                SqliteCommand updateBatch = connection.CreateCommand();
                await using (updateBatch.ConfigureAwait(false))
                {
                    updateBatch.Transaction = transaction;
                    updateBatch.CommandText = """
                        UPDATE maintenance_batch
                        SET status = $status, completed_utc = $completedUtc, summary_json = $summaryJson
                        WHERE id = $id;
                        """;
                    updateBatch.Parameters.AddWithValue("$id", batch.Id);
                    updateBatch.Parameters.AddWithValue("$status", (int)batch.Status);
                    updateBatch.Parameters.AddWithValue("$completedUtc", FormatNullableDate(batch.CompletedUtc));
                    updateBatch.Parameters.AddWithValue("$summaryJson", batch.SummaryJson);
                    await updateBatch.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                }

                await UpsertQuarantineItemsAsync(connection, transaction, batch, token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return 0;
            },
            cancellationToken);
    }

    /// <summary>Reads live SQLite journal, schema, file-size, and corruption-recovery diagnostics for administrators.</summary>
    /// <param name="cancellationToken">A token that cancels the diagnostic query.</param>
    /// <returns>The current database health snapshot.</returns>
    /// <exception cref="InvalidOperationException">Thrown when SQLite does not return the required PRAGMA values.</exception>
    public async Task<FontDatabaseHealth> GetDatabaseHealthAsync(CancellationToken cancellationToken)
    {
        SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);
        SqliteCommand command = connection.CreateCommand();
        await using var commandScope = command.ConfigureAwait(false);
        command.CommandText = "SELECT (SELECT journal_mode FROM pragma_journal_mode), (SELECT user_version FROM pragma_user_version), published_generation_id FROM schema_info WHERE singleton_id = 1;";
        SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var readerScope = reader.ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("SQLite did not return database health pragmas.");
        }

        return new FontDatabaseHealth
        {
            DatabasePath = _database.DatabasePath,
            SchemaVersion = reader.GetInt32(1),
            JournalMode = reader.GetString(0),
            Size = File.Exists(_database.DatabasePath) ? new FileInfo(_database.DatabasePath).Length : 0,
            PublishedGenerationId = await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt64(2),
            CheckedUtc = DateTimeOffset.UtcNow,
            RecoveredCorruptDatabasePath = _database.RecoveredCorruptDatabasePath
        };
    }

    private static async Task EnsureBuildingGenerationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long generationId,
        CancellationToken cancellationToken)
    {
        SqliteCommand command = connection.CreateCommand();
        await using var commandScope = command.ConfigureAwait(false);
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM scan_generation WHERE id = $generationId AND status = 0;";
        command.Parameters.AddWithValue("$generationId", generationId);
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (Convert.ToInt64(result, CultureInfo.InvariantCulture) != 1)
        {
            throw new InvalidOperationException("Inventory can only be written to a building generation.");
        }
    }

    private static async Task UpsertQuarantineItemsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MaintenanceBatch batch,
        CancellationToken cancellationToken)
    {
        foreach (RenameItemRecord item in batch.RenameItems)
        {
            SqliteCommand renameCommand = connection.CreateCommand();
            await using var renameCommandScope = renameCommand.ConfigureAwait(false);
            renameCommand.Transaction = transaction;
            renameCommand.CommandText = """
                INSERT INTO rename_item(maintenance_batch_id, file_id, old_path, new_path, sha256, status, error, item_json, source_key, category, generation_id, physical_identity, source_path_key, target_path_key)
                VALUES ($batch, $file, $old, $new, $hash, $status, $error, $json, $sourceKey, $category, $generation, $identity, $sourcePathKey, $targetPathKey)
                ON CONFLICT(maintenance_batch_id, file_id) DO UPDATE SET status=excluded.status, error=excluded.error, item_json=excluded.item_json, source_key=excluded.source_key, category=excluded.category;
                """;
            renameCommand.Parameters.AddWithValue("$batch", batch.Id);
            renameCommand.Parameters.AddWithValue("$file", item.FileId);
            renameCommand.Parameters.AddWithValue("$old", item.OldPath);
            renameCommand.Parameters.AddWithValue("$new", item.NewPath);
            renameCommand.Parameters.AddWithValue("$hash", item.Evidence.Sha256.ToArray());
            renameCommand.Parameters.AddWithValue("$status", (int)item.Status);
            renameCommand.Parameters.AddWithValue("$error", (object?)item.Error ?? DBNull.Value);
            renameCommand.Parameters.AddWithValue("$generation", item.Evidence.GenerationId);
            renameCommand.Parameters.AddWithValue("$identity", item.Evidence.PhysicalIdentity);
            renameCommand.Parameters.AddWithValue("$sourcePathKey", FontRenameStateIdentity.PathKey(item.OldPath));
            renameCommand.Parameters.AddWithValue("$targetPathKey", FontRenameStateIdentity.PathKey(item.NewPath));
            renameCommand.Parameters.AddWithValue("$sourceKey", FontRenameStateIdentity.SourceKey(item.OldPath, item.NewPath, item.Evidence));
            renameCommand.Parameters.AddWithValue("$category", FontRenameStateIdentity.Category(item.Status, item.Error));
            renameCommand.Parameters.AddWithValue("$json", JsonSerializer.Serialize(item));
            await renameCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (QuarantineItemRecord item in batch.Items)
        {
            SqliteCommand command = connection.CreateCommand();
            await using var commandScope = command.ConfigureAwait(false);
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO quarantine_item(
                    maintenance_batch_id, file_id, source_path, staging_path, quarantine_path,
                    sha256, size, status, quarantined_utc, expires_utc, restored_utc, error)
                VALUES (
                    $batchId, $fileId, $sourcePath, $stagingPath, $quarantinePath,
                    $sha256, $size, $status, $quarantinedUtc, $expiresUtc, $restoredUtc, $error)
                ON CONFLICT(maintenance_batch_id, file_id) DO UPDATE SET
                    source_path = excluded.source_path,
                    staging_path = excluded.staging_path,
                    quarantine_path = excluded.quarantine_path,
                    sha256 = excluded.sha256,
                    size = excluded.size,
                    status = excluded.status,
                    quarantined_utc = excluded.quarantined_utc,
                    expires_utc = excluded.expires_utc,
                    restored_utc = excluded.restored_utc,
                    error = excluded.error;
                """;
            command.Parameters.AddWithValue("$batchId", batch.Id);
            command.Parameters.AddWithValue("$fileId", item.FileId);
            command.Parameters.AddWithValue("$sourcePath", item.SourcePath);
            command.Parameters.AddWithValue("$stagingPath", item.StagingPath);
            command.Parameters.AddWithValue("$quarantinePath", item.QuarantinePath);
            command.Parameters.AddWithValue("$sha256", item.Sha256.ToArray());
            command.Parameters.AddWithValue("$size", item.Size);
            command.Parameters.AddWithValue("$status", (int)item.Status);
            command.Parameters.AddWithValue("$quarantinedUtc", FormatNullableDate(item.QuarantinedUtc));
            command.Parameters.AddWithValue("$expiresUtc", FormatNullableDate(item.ExpiresUtc));
            command.Parameters.AddWithValue("$restoredUtc", FormatNullableDate(item.RestoredUtc));
            command.Parameters.AddWithValue("$error", item.Error is null ? DBNull.Value : item.Error);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task InsertRelationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long batchId,
        FontRelationRecord relation,
        CancellationToken cancellationToken)
    {
        SqliteCommand command = connection.CreateCommand();
        await using var commandScope = command.ConfigureAwait(false);
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO font_relation(analysis_batch_id, left_face_id, right_face_id, relationship_kind, replacement_direction, evidence_version, evidence_json)
            VALUES ($batchId, $leftFaceId, $rightFaceId, $kind, $direction, $evidenceVersion, $evidenceJson);
            """;
        command.Parameters.AddWithValue("$batchId", batchId);
        command.Parameters.AddWithValue("$leftFaceId", relation.LeftFaceId);
        command.Parameters.AddWithValue("$rightFaceId", relation.RightFaceId);
        command.Parameters.AddWithValue("$kind", (int)relation.RelationshipKind);
        command.Parameters.AddWithValue("$direction", (int)relation.ReplacementDirection);
        command.Parameters.AddWithValue("$evidenceVersion", relation.EvidenceVersion);
        command.Parameters.AddWithValue("$evidenceJson", relation.EvidenceJson);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertGroupAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long batchId,
        DuplicateGroupRecord group,
        CancellationToken cancellationToken)
    {
        SqliteCommand groupCommand = connection.CreateCommand();
        await using var groupCommandScope = groupCommand.ConfigureAwait(false);
        groupCommand.Transaction = transaction;
        groupCommand.CommandText = """
            INSERT INTO duplicate_group(analysis_batch_id, retained_face_id, reclaimable_bytes, evidence_json)
            VALUES ($batchId, $retainedFaceId, $reclaimableBytes, $evidenceJson);
            SELECT last_insert_rowid();
            """;
        groupCommand.Parameters.AddWithValue("$batchId", batchId);
        groupCommand.Parameters.AddWithValue("$retainedFaceId", group.RetainedFaceId);
        groupCommand.Parameters.AddWithValue("$reclaimableBytes", group.ReclaimableBytes);
        groupCommand.Parameters.AddWithValue("$evidenceJson", group.EvidenceJson);
        object? result = await groupCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        long groupId = Convert.ToInt64(result, CultureInfo.InvariantCulture);

        foreach (DuplicateGroupMemberRecord member in group.Members)
        {
            SqliteCommand memberCommand = connection.CreateCommand();
            await using var memberCommandScope = memberCommand.ConfigureAwait(false);
            memberCommand.Transaction = transaction;
            memberCommand.CommandText = """
                INSERT INTO group_member(duplicate_group_id, face_id, file_id, decision, retention_score, score_json, reason)
                VALUES ($groupId, $faceId, $fileId, $decision, $retentionScore, $scoreJson, $reason);
                """;
            memberCommand.Parameters.AddWithValue("$groupId", groupId);
            memberCommand.Parameters.AddWithValue("$faceId", member.FaceId);
            memberCommand.Parameters.AddWithValue("$fileId", member.FileId);
            memberCommand.Parameters.AddWithValue("$decision", (int)member.Decision);
            memberCommand.Parameters.AddWithValue("$retentionScore", member.RetentionScore);
            memberCommand.Parameters.AddWithValue("$scoreJson", member.ScoreJson);
            memberCommand.Parameters.AddWithValue("$reason", member.Reason);
            await memberCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static FontFaceAnalysisRecord[] ReadFaceScores(string summaryJson, AnalysisBatchStatus status)
    {
        try
        {
            using JsonDocument summary = JsonDocument.Parse(summaryJson);
            if (summary.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("An analysis summary must be a JSON object.");
            }

            if (!summary.RootElement.TryGetProperty("faceScores", out JsonElement scores))
            {
                if (status is AnalysisBatchStatus.Running or AnalysisBatchStatus.Failed)
                {
                    return [];
                }

                throw new InvalidDataException("A completed analysis summary requires current Face evidence.");
            }

            FontFaceAnalysisRecord[] records = scores.Deserialize<FontFaceAnalysisRecord[]>(JsonSerializerOptions.Web)
                ?? throw new InvalidDataException("Analysis Face evidence cannot be null.");
            if (records.Any(record => record is null))
            {
                throw new InvalidDataException("Analysis Face evidence cannot contain null records.");
            }

            return records;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("An analysis summary contains invalid Face evidence.", exception);
        }
    }

    private static async Task<OperationRequest?> FindOperationByIdempotencyKeyAsync(SqliteConnection connection, string key, CancellationToken cancellationToken)
    {
        SqliteCommand command = connection.CreateCommand();
        await using var commandScope = command.ConfigureAwait(false);
        command.CommandText = """
            SELECT id, operation_kind, status, analysis_batch_id, idempotency_key, request_json,
                   created_utc, claimed_utc, completed_utc, error, result_json
            FROM operation_request
            WHERE idempotency_key = $key;
            """;
        command.Parameters.AddWithValue("$key", key);
        SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var readerScope = reader.ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadOperationRequest(reader) : null;
    }

    private static void AddOperationParameters(SqliteCommand command, OperationRequest request)
    {
        command.Parameters.AddWithValue("$kind", (int)request.Kind);
        command.Parameters.AddWithValue("$status", (int)request.Status);
        command.Parameters.AddWithValue("$analysisBatchId", request.AnalysisBatchId is long batchId ? batchId : DBNull.Value);
        command.Parameters.AddWithValue("$idempotencyKey", request.IdempotencyKey);
        command.Parameters.AddWithValue("$requestJson", request.RequestJson);
        command.Parameters.AddWithValue("$createdUtc", FormatDate(request.CreatedUtc));
        command.Parameters.AddWithValue("$claimedUtc", FormatNullableDate(request.ClaimedUtc));
        command.Parameters.AddWithValue("$completedUtc", FormatNullableDate(request.CompletedUtc));
        command.Parameters.AddWithValue("$error", (object?)request.Error ?? DBNull.Value);
        command.Parameters.AddWithValue("$resultJson", (object?)request.ResultJson ?? DBNull.Value);
    }

    private static async Task PublishGenerationCoreAsync(SqliteConnection connection, SqliteTransaction transaction, long generationId, CancellationToken cancellationToken)
    {
        SqliteCommand command = connection.CreateCommand();
        await using var commandScope = command.ConfigureAwait(false);
        command.Transaction = transaction;
        command.CommandText = "UPDATE scan_generation SET status = 1, published_utc = $publishedUtc WHERE id = $generationId AND status = 0;";
        command.Parameters.AddWithValue("$generationId", generationId);
        command.Parameters.AddWithValue("$publishedUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        int changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (changed != 1)
        {
            throw new InvalidOperationException("Only a building generation can be published.");
        }

        command.CommandText = "UPDATE scan_generation SET status = 2 WHERE status = 1 AND id <> $generationId;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        command.CommandText = "UPDATE schema_info SET published_generation_id = $generationId WHERE singleton_id = 1;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static OperationRequest ReadOperationRequest(SqliteDataReader reader)
    {
        return new OperationRequest
        {
            Id = reader.GetInt64(0),
            Kind = (FontOperationKind)reader.GetInt32(1),
            Status = (OperationRequestStatus)reader.GetInt32(2),
            AnalysisBatchId = reader.IsDBNull(3) ? null : reader.GetInt64(3),
            IdempotencyKey = reader.GetString(4),
            RequestJson = reader.GetString(5),
            CreatedUtc = ParseDate(reader.GetString(6)),
            ClaimedUtc = reader.IsDBNull(7) ? null : ParseDate(reader.GetString(7)),
            CompletedUtc = reader.IsDBNull(8) ? null : ParseDate(reader.GetString(8)),
            Error = reader.IsDBNull(9) ? null : reader.GetString(9),
            ResultJson = reader.IsDBNull(10) ? null : reader.GetString(10)
        };
    }

    private static string FormatDate(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static object FormatNullableDate(DateTimeOffset? value) => value is DateTimeOffset date ? FormatDate(date) : DBNull.Value;

    private static DateTimeOffset ParseDate(string value) => DateTimeOffset.ParseExact(value, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
