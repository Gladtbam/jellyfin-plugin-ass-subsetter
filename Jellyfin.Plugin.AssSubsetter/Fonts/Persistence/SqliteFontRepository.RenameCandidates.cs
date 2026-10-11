using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <summary>Stores derived rename candidates separately from execution authorization.</summary>
public sealed partial class SqliteFontRepository
{
    internal const string RenameStateRowsSql = """
        WITH raw AS (
            SELECT 1 row_type, r.id row_id, r.file_id, r.generation_id, r.source_key, r.old_path, r.new_path,
                   r.category, r.status, r.error, b.id maintenance_batch_id, b.operation_request_id request_id
            FROM rename_item r JOIN maintenance_batch b ON b.id = r.maintenance_batch_id
            UNION ALL
            SELECT 2, r.id, r.file_id, r.generation_id, r.source_key, r.old_path, r.new_path,
                   CASE WHEN o.status IN ($opPending, $opRunning) AND o.error IS NULL THEN 'pending' WHEN o.status = $opFailed OR (o.status IN ($opPending, $opRunning) AND o.error IS NOT NULL) THEN 'failed' ELSE 'skipped' END,
                   CASE WHEN o.status IN ($opPending, $opRunning) THEN $pending WHEN o.status = $opFailed THEN $failedIo ELSE $skipped END,
                   COALESCE(o.error, CASE WHEN o.status = $opCancelled THEN 'request-cancelled' ELSE NULL END), NULL, o.id
            FROM rename_request_item r JOIN operation_request o ON o.id = r.request_id
            WHERE NOT EXISTS (
                SELECT 1 FROM maintenance_batch b JOIN rename_item recorded ON recorded.maintenance_batch_id = b.id
                WHERE b.operation_request_id = r.request_id AND recorded.file_id = r.file_id)
        ), attempts AS (
            SELECT *, ROW_NUMBER() OVER (PARTITION BY source_key ORDER BY request_id DESC, maintenance_batch_id DESC) attempt_rank FROM raw
        ), latest AS (
            SELECT * FROM attempts WHERE attempt_rank = 1
        ), active_paths AS MATERIALIZED (
            SELECT r.source_path_key path FROM rename_request_item r JOIN operation_request o ON o.id = r.request_id WHERE o.operation_kind = $normalize AND o.status IN ($opPending, $opRunning)
            UNION
            SELECT r.target_path_key FROM rename_request_item r JOIN operation_request o ON o.id = r.request_id WHERE o.operation_kind = $normalize AND o.status IN ($opPending, $opRunning)
        ), active_sources AS MATERIALIZED (
            SELECT DISTINCT r.physical_identity FROM rename_request_item r JOIN operation_request o ON o.id = r.request_id
            WHERE r.physical_identity <> '' AND o.operation_kind = $normalize AND o.status IN ($opPending, $opRunning)
        ), visible AS (
            SELECT r.row_type, r.row_id, r.file_id, r.generation_id, r.maintenance_batch_id, r.request_id, r.status, r.error, r.old_path, r.source_key,
                   'execution:' || r.request_id || ':' || COALESCE(r.maintenance_batch_id, '') || ':' || r.source_key row_key,
                   0 can_execute,
                   CASE WHEN r.status = $renamed AND r.maintenance_batch_id IS NOT NULL AND NOT EXISTS (
                       SELECT 1 FROM latest l WHERE l.source_key = r.source_key AND l.request_id > r.request_id AND l.status IN ($rolledBack, $rollingBack))
                       THEN 1 ELSE 0 END can_rollback
            FROM attempts r WHERE r.category = $category AND ($history = 1 OR r.attempt_rank = 1)
            UNION ALL
            SELECT 0, c.file_id, c.file_id, $generation, NULL, NULL, CASE WHEN c.category = 'pending' THEN $pending ELSE $skipped END,
                   c.skip_reason, c.old_path, c.source_key, 'current:' || $generation || ':' || $rules || ':' || c.source_key,
                   CASE WHEN c.category = 'pending' THEN 1 ELSE 0 END, 0
            FROM rename_candidate c WHERE $current = 1 AND $history = 0 AND c.category = $category
                AND NOT EXISTS (SELECT 1 FROM latest l WHERE l.source_key = c.source_key AND (l.category = 'pending' OR l.category = c.category))
                AND NOT EXISTS (SELECT 1 FROM active_paths a WHERE a.path = c.source_path_key)
                AND NOT EXISTS (SELECT 1 FROM active_paths a WHERE a.path = c.target_path_key)
                AND NOT EXISTS (SELECT 1 FROM active_sources a WHERE a.physical_identity = c.physical_identity)
        )
        """;

    /// <inheritdoc/>
    public Task RecoverRenameCandidateRefreshAsync(CancellationToken cancellationToken)
        => _database.WriteAsync(
            async (connection, token) =>
            {
                SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
                await using var transactionScope = transaction.ConfigureAwait(false);
                SqliteCommand command = connection.CreateCommand();
                await using var commandScope = command.ConfigureAwait(false);
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE operation_request SET status = $failed, completed_utc = $completedUtc, error = $interrupted
                    WHERE operation_kind = $analysisKind AND status = $running AND id IN (
                        SELECT operation_request_id FROM rename_candidate_state WHERE status IN ($statePending, $stateRunning));
                    UPDATE rename_candidate_state
                    SET status = $stateStale, state_json = json_set(state_json, '$.Status', $stateStale, '$.Error', $interrupted)
                    WHERE status IN ($statePending, $stateRunning) AND operation_request_id IN (
                        SELECT id FROM operation_request WHERE operation_kind = $analysisKind AND status = $failed AND error = $interrupted);
                    UPDATE operation_request SET status = $pending, claimed_utc = NULL
                    WHERE operation_kind = $kind AND status = $running;
                    UPDATE rename_candidate_state SET status = $statePending, state_json = json_set(state_json, '$.Status', $statePending)
                    WHERE status = $stateRunning AND operation_request_id IN (
                        SELECT id FROM operation_request WHERE operation_kind = $kind AND status = $pending);
                    """;
                command.Parameters.AddWithValue("$kind", (int)FontOperationKind.RefreshRenameCandidates);
                command.Parameters.AddWithValue("$pending", (int)OperationRequestStatus.Pending);
                command.Parameters.AddWithValue("$running", (int)OperationRequestStatus.Running);
                command.Parameters.AddWithValue("$statePending", (int)FontRenameCandidateGenerationStatus.Pending);
                command.Parameters.AddWithValue("$stateRunning", (int)FontRenameCandidateGenerationStatus.Running);
                command.Parameters.AddWithValue("$analysisKind", (int)FontOperationKind.Analyze);
                command.Parameters.AddWithValue("$failed", (int)OperationRequestStatus.Failed);
                command.Parameters.AddWithValue("$stateStale", (int)FontRenameCandidateGenerationStatus.Stale);
                command.Parameters.AddWithValue("$completedUtc", FormatNullableDate(DateTimeOffset.UtcNow));
                command.Parameters.AddWithValue("$interrupted", "The analysis task was interrupted during rename candidate generation.");
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    /// <inheritdoc/>
    public Task<OperationRequest?> CreateRenameCandidateRefreshAsync(OperationRequest request, FontNamingRules rules, FontRenameCandidateReadRequirements requirements, bool onlyIfNeeded, CancellationToken cancellationToken)
        => _database.WriteAsync<OperationRequest?>(
            async (connection, token) =>
            {
                var existing = await FindOperationByIdempotencyKeyAsync(connection, request.IdempotencyKey, token).ConfigureAwait(false);
                if (existing is not null)
                {
                    if (existing.Kind != FontOperationKind.RefreshRenameCandidates)
                    {
                        throw new InvalidOperationException("The idempotency key belongs to another operation.");
                    }

                    return existing;
                }

                SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
                await using var transactionScope = transaction.ConfigureAwait(false);
                var context = await ReadRenameCandidateContextAsync(connection, transaction, requirements, token).ConfigureAwait(false);
                var proposed = ParseRenameRefresh(request.RequestJson);
                if (!RefreshMatchesPublication(context, proposed, requirements) || proposed.RuleFingerprint != rules.GetFingerprint()
                    || proposed.CalculationRequestId is not null || request.Kind != FontOperationKind.RefreshRenameCandidates || request.Id != 0)
                {
                    if (onlyIfNeeded)
                    {
                        return null;
                    }

                    throw new InvalidOperationException("The source analysis or naming inputs changed before acceptance.");
                }

                bool same = context.State is not null && context.State.Status != FontRenameCandidateGenerationStatus.Stale;
                SqliteCommand command = connection.CreateCommand();
                await using var commandScope = command.ConfigureAwait(false);
                command.Transaction = transaction;
                long? root = null;
                OperationRequestStatus? rootStatus = null;
                string? rootError = null;
                if (same && context.State!.OperationRequestId is long active)
                {
                    command.CommandText = "SELECT status, operation_kind, error FROM operation_request WHERE id = $id;";
                    command.Parameters.AddWithValue("$id", active);
                    SqliteDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                    await using var readerScope = reader.ConfigureAwait(false);
                    if (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        rootStatus = (OperationRequestStatus)reader.GetInt32(0);
                        rootError = await reader.IsDBNullAsync(2, token).ConfigureAwait(false) ? null : reader.GetString(2);
                        if ((FontOperationKind)reader.GetInt32(1) == FontOperationKind.RefreshRenameCandidates && rootStatus is OperationRequestStatus.Pending or OperationRequestStatus.Running)
                        {
                            root = active;
                        }
                    }
                }

                if (onlyIfNeeded && same)
                {
                    if (context.State!.Status is FontRenameCandidateGenerationStatus.Pending or FontRenameCandidateGenerationStatus.Running
                        && rootStatus is OperationRequestStatus.Complete or OperationRequestStatus.Failed or OperationRequestStatus.Cancelled)
                    {
                        await WriteRenameCandidateStateAsync(connection, transaction, context.State with { Status = FontRenameCandidateGenerationStatus.Failed, Error = rootError ?? "The candidate calculation ended without a committed result." }, token).ConfigureAwait(false);
                        await transaction.CommitAsync(token).ConfigureAwait(false);
                    }

                    return null;
                }

                request = request with { RequestJson = JsonSerializer.Serialize(proposed with { CalculationRequestId = root }, JsonSerializerOptions.Web) };
                command.Parameters.Clear();
                command.CommandText = """
                    INSERT INTO operation_request(operation_kind, status, analysis_batch_id, idempotency_key, request_json, created_utc, claimed_utc, completed_utc, error, result_json)
                    VALUES ($kind, $status, $analysisBatchId, $idempotencyKey, $requestJson, $createdUtc, $claimedUtc, $completedUtc, $error, $resultJson);
                    SELECT last_insert_rowid();
                    """;
                AddOperationParameters(command, request);
                var result = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
                var persisted = request with { Id = Convert.ToInt64(result, CultureInfo.InvariantCulture) };
                if (root is null)
                {
                    var state = new FontRenameCandidateState(proposed.GenerationId, proposed.AnalysisBatchId, rules, rules.GetFingerprint(), proposed.CalculatorVersion, FontRenameCandidateGenerationStatus.Pending, context.State?.GeneratedUtc, persisted.Id, null);
                    await WriteRenameCandidateStateAsync(connection, transaction, state, token).ConfigureAwait(false);
                }

                await transaction.CommitAsync(token).ConfigureAwait(false);
                return persisted;
            },
            cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<OperationRequest>> ClaimRenameCandidateRefreshGroupAsync(CancellationToken cancellationToken)
        => _database.WriteAsync<IReadOnlyList<OperationRequest>>(
            async (connection, token) =>
            {
                SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
                await using var transactionScope = transaction.ConfigureAwait(false);
                SqliteCommand command = connection.CreateCommand();
                await using var commandScope = command.ConfigureAwait(false);
                command.Transaction = transaction;
                command.CommandText = """
                    SELECT id, operation_kind, status, analysis_batch_id, idempotency_key, request_json, created_utc, claimed_utc, completed_utc, error, result_json
                    FROM operation_request WHERE operation_kind = $kind AND status = $pending
                    AND COALESCE(json_extract(request_json, '$.calculationRequestId'), id) = (
                        SELECT COALESCE(json_extract(request_json, '$.calculationRequestId'), id)
                        FROM operation_request WHERE operation_kind = $kind AND status = $pending ORDER BY id LIMIT 1)
                    ORDER BY id;
                    """;
                command.Parameters.AddWithValue("$kind", (int)FontOperationKind.RefreshRenameCandidates);
                command.Parameters.AddWithValue("$pending", (int)OperationRequestStatus.Pending);
                var group = new List<OperationRequest>();
                DateTimeOffset claimed = DateTimeOffset.UtcNow;
                {
                    SqliteDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
                    await using var readerScope = reader.ConfigureAwait(false);
                    while (await reader.ReadAsync(token).ConfigureAwait(false))
                    {
                        group.Add(ReadOperationRequest(reader) with { Status = OperationRequestStatus.Running, ClaimedUtc = claimed });
                    }
                }

                command.CommandText = "UPDATE operation_request SET status = $running, claimed_utc = $claimed WHERE id = $id AND status = $pending;";
                command.Parameters.AddWithValue("$running", (int)OperationRequestStatus.Running);
                command.Parameters.AddWithValue("$claimed", FormatDate(claimed));
                command.Parameters.AddWithValue("$id", 0L);
                foreach (var request in group)
                {
                    command.Parameters["$id"].Value = request.Id;
                    if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                    {
                        throw new InvalidOperationException("Candidate refresh group could not be claimed.");
                    }
                }

                await transaction.CommitAsync(token).ConfigureAwait(false);
                return group;
            },
            cancellationToken);

    internal static FontRenameCandidateRefreshPayload ParseRenameRefresh(string json)
        => JsonSerializer.Deserialize<FontRenameCandidateRefreshPayload>(json, JsonSerializerOptions.Web) ?? throw new InvalidOperationException("Invalid candidate refresh request.");

    internal static bool RefreshMatchesPublication(FontRenameCandidateReadContext context, FontRenameCandidateRefreshPayload payload, FontRenameCandidateReadRequirements requirements)
        => context.Publication is { } publication && publication.GenerationId == payload.GenerationId && publication.AnalysisBatchId == payload.AnalysisBatchId
            && publication.EvidenceVersion == requirements.AnalysisEvidenceVersion && publication.Input.PolicyFingerprint == requirements.PolicyFingerprint
            && publication.Input.EvidenceFingerprint == requirements.EvidenceFingerprint && payload.RuleFingerprint == requirements.RuleFingerprint
            && payload.CalculatorVersion == requirements.CalculatorVersion;

    /// <inheritdoc/>
    public async Task<FontRenameStatePage> LoadRenameStatePageAsync(string status, int page, int pageSize, FontRenameCandidateReadRequirements requirements, CancellationToken cancellationToken)
    {
        ValidateRenameStateFilter(status);
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);
        SqliteTransaction transaction = BeginRenameReadTransaction(connection);
        await using var transactionScope = transaction.ConfigureAwait(false);
        var context = await ReadRenameCandidateContextAsync(connection, transaction, requirements, cancellationToken).ConfigureAwait(false);
        SqliteCommand command = connection.CreateCommand();
        await using var commandScope = command.ConfigureAwait(false);
        command.Transaction = transaction;
        AddRenameStateParameters(command, status, context);
        command.CommandText = RenameStateRowsSql + "SELECT COUNT(*) FROM visible;";
        long total = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        command.CommandText = RenameStateRowsSql + """
            SELECT v.row_key, v.row_type, v.file_id, v.generation_id, v.maintenance_batch_id, v.request_id,
                   v.status, v.error, v.can_execute, v.can_rollback,
                   CASE v.row_type
                       WHEN 0 THEN (SELECT item_json FROM rename_candidate WHERE file_id = v.row_id)
                       WHEN 1 THEN (SELECT item_json FROM rename_item WHERE id = v.row_id)
                       ELSE (SELECT item_json FROM rename_request_item WHERE id = v.row_id)
                   END
            FROM visible v ORDER BY v.old_path, v.row_key LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$limit", pageSize);
        command.Parameters.AddWithValue("$offset", ((long)page - 1) * pageSize);
        var rows = new List<FontRenameStateRow>(pageSize);
        SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var readerScope = reader.ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            bool candidate = reader.GetInt32(1) == 0;
            RenameItemRecord item = candidate ? CandidateStateItem(JsonSerializer.Deserialize<FontRenameCandidate>(reader.GetString(10))!)
                : JsonSerializer.Deserialize<RenameItemRecord>(reader.GetString(10))!;
            long? batchId = await reader.IsDBNullAsync(4, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt64(4);
            long? requestId = await reader.IsDBNullAsync(5, cancellationToken).ConfigureAwait(false) ? null : reader.GetInt64(5);
            item = item with { MaintenanceBatchId = batchId, OperationRequestId = requestId, Status = (RenameItemStatus)reader.GetInt32(6), Error = await reader.IsDBNullAsync(7, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(7) };
            rows.Add(new(reader.GetString(0), batchId, requestId, reader.GetInt64(3), reader.GetInt64(2), reader.GetInt32(8) != 0, reader.GetInt32(9) != 0, item));
        }

        return new(context, rows, total);
    }

    internal static void ValidateRenameStateFilter(string status)
    {
        if (status is not "pending" and not "skipped" and not "failed" and not "renamed" and not "rolledback")
        {
            throw new ArgumentException("Unknown filename status filter.", nameof(status));
        }
    }

    internal static void AddRenameStateParameters(SqliteCommand command, string status, FontRenameCandidateReadContext context)
    {
        command.Parameters.AddWithValue("$category", status);
        command.Parameters.AddWithValue("$history", status is "renamed" or "rolledback" ? 1 : 0);
        command.Parameters.AddWithValue("$current", context.IsCurrent ? 1 : 0);
        command.Parameters.AddWithValue("$generation", context.State?.GenerationId ?? 0);
        command.Parameters.AddWithValue("$rules", context.State?.RuleFingerprint ?? string.Empty);
        command.Parameters.AddWithValue("$normalize", (int)FontOperationKind.NormalizeNames);
        command.Parameters.AddWithValue("$opPending", (int)OperationRequestStatus.Pending);
        command.Parameters.AddWithValue("$opRunning", (int)OperationRequestStatus.Running);
        command.Parameters.AddWithValue("$opFailed", (int)OperationRequestStatus.Failed);
        command.Parameters.AddWithValue("$opCancelled", (int)OperationRequestStatus.Cancelled);
        command.Parameters.AddWithValue("$pending", (int)RenameItemStatus.Pending);
        command.Parameters.AddWithValue("$skipped", (int)RenameItemStatus.Skipped);
        command.Parameters.AddWithValue("$failedIo", (int)RenameItemStatus.FailedIo);
        command.Parameters.AddWithValue("$renamed", (int)RenameItemStatus.Renamed);
        command.Parameters.AddWithValue("$rolledBack", (int)RenameItemStatus.RolledBack);
        command.Parameters.AddWithValue("$rollingBack", (int)RenameItemStatus.RollingBack);
    }

    private static RenameItemRecord CandidateStateItem(FontRenameCandidate candidate)
        => new() { FileId = candidate.File.Id, Evidence = candidate.File, OldPath = candidate.File.Path, NewPath = candidate.NewPath ?? string.Empty, SourceRoot = candidate.SourceRoot ?? string.Empty, NamingEvidence = candidate.NamingEvidence, CollisionFingerprint = candidate.CollisionFingerprint, Status = candidate.SkipReason is null ? RenameItemStatus.Pending : RenameItemStatus.Skipped, Error = candidate.SkipReason };

    private static async Task ValidateActiveRenameReservationAsync(SqliteConnection connection, SqliteTransaction transaction, FontRenameExecutionPayload proposed, CancellationToken token)
    {
        SqliteCommand command = connection.CreateCommand();
        await using var commandScope = command.ConfigureAwait(false);
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1 FROM rename_request_item r JOIN operation_request o ON o.id = r.request_id
                WHERE r.source_path_key IN ($source, $target) AND o.operation_kind = $kind AND o.status IN ($pending, $running)
                UNION ALL
                SELECT 1 FROM rename_request_item r JOIN operation_request o ON o.id = r.request_id
                WHERE r.target_path_key IN ($source, $target) AND o.operation_kind = $kind AND o.status IN ($pending, $running)
                UNION ALL
                SELECT 1 FROM rename_request_item r JOIN operation_request o ON o.id = r.request_id
                WHERE $identity <> '' AND r.physical_identity = $identity AND o.operation_kind = $kind AND o.status IN ($pending, $running)
                LIMIT 1);
            """;
        foreach (var item in proposed.Items)
        {
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$source", FontRenameStateIdentity.PathKey(item.OldPath));
            command.Parameters.AddWithValue("$target", FontRenameStateIdentity.PathKey(item.NewPath));
            command.Parameters.AddWithValue("$identity", item.Evidence.PhysicalIdentity);
            command.Parameters.AddWithValue("$kind", (int)FontOperationKind.NormalizeNames);
            command.Parameters.AddWithValue("$pending", (int)OperationRequestStatus.Pending);
            command.Parameters.AddWithValue("$running", (int)OperationRequestStatus.Running);
            if (Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture) != 0)
            {
                throw new InvalidOperationException("A source file or target path already has active filename authorization.");
            }
        }
    }

    private static async Task InsertRenameRequestItemsAsync(SqliteConnection connection, SqliteTransaction transaction, OperationRequest request, CancellationToken token)
    {
        if (request.Kind != FontOperationKind.NormalizeNames)
        {
            return;
        }

        var payload = FontOperationRequestPayloadCodec.ParseRenameExecution(request.RequestJson);
        SqliteCommand command = connection.CreateCommand();
        await using var commandScope = command.ConfigureAwait(false);
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO rename_request_item(request_id, file_id, generation_id, physical_identity, source_key, old_path, new_path, source_path_key, target_path_key, item_json)
            VALUES ($request, $file, $generation, $identity, $key, $old, $new, $sourcePathKey, $targetPathKey, $json);
            """;
        foreach (var item in payload.Items)
        {
            token.ThrowIfCancellationRequested();
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$request", request.Id);
            command.Parameters.AddWithValue("$file", item.FileId);
            command.Parameters.AddWithValue("$generation", item.Evidence.GenerationId);
            command.Parameters.AddWithValue("$identity", item.Evidence.PhysicalIdentity);
            command.Parameters.AddWithValue("$key", FontRenameStateIdentity.SourceKey(item.OldPath, item.NewPath, item.Evidence));
            command.Parameters.AddWithValue("$old", item.OldPath);
            command.Parameters.AddWithValue("$new", item.NewPath);
            command.Parameters.AddWithValue("$sourcePathKey", FontRenameStateIdentity.PathKey(item.OldPath));
            command.Parameters.AddWithValue("$targetPathKey", FontRenameStateIdentity.PathKey(item.NewPath));
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(item));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task<FontRenameCandidateReadContext> GetRenameCandidateContextAsync(FontRenameCandidateReadRequirements requirements, CancellationToken cancellationToken)
    {
        SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);
        SqliteTransaction transaction = BeginRenameReadTransaction(connection);
        await using var transactionScope = transaction.ConfigureAwait(false);
        return await ReadRenameCandidateContextAsync(connection, transaction, requirements, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only generated parameter names are interpolated; all IDs are bound parameters.")]
    public async Task<FontRenameCandidateSelection> LoadRenameCandidatesAsync(IReadOnlyList<long>? fileIds, FontRenameCandidateReadRequirements requirements, CancellationToken cancellationToken)
    {
        SqliteConnection connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);
        SqliteTransaction transaction = BeginRenameReadTransaction(connection);
        await using var transactionScope = transaction.ConfigureAwait(false);
        var context = await ReadRenameCandidateContextAsync(connection, transaction, requirements, cancellationToken).ConfigureAwait(false);
        var items = new List<FontRenameCandidate>();
        if (!context.IsCurrent || fileIds is { Count: 0 })
        {
            return new(context, items);
        }

        IEnumerable<long[]?> chunks = fileIds is null ? new long[]?[] { null } : fileIds.Distinct().Chunk(200).Select(chunk => (long[]?)chunk);
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SqliteCommand command = connection.CreateCommand();
            await using var commandScope = command.ConfigureAwait(false);
            command.Transaction = transaction;
            command.CommandText = "SELECT item_json FROM rename_candidate";
            if (chunk is not null)
            {
                string[] names = chunk.Select((_, index) => "$id" + index.ToString(CultureInfo.InvariantCulture)).ToArray();
                command.CommandText += " WHERE file_id IN (" + string.Join(",", names) + ")";
                for (int index = 0; index < chunk.Length; index++)
                {
                    command.Parameters.AddWithValue(names[index], chunk[index]);
                }
            }

            command.CommandText += " ORDER BY file_id;";
            SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using var readerScope = reader.ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(JsonSerializer.Deserialize<FontRenameCandidate>(reader.GetString(0))!);
            }
        }

        return new(context, items.OrderBy(item => item.File.Id).ToArray());
    }

    /// <inheritdoc/>
    public Task SetRenameCandidateStateAsync(FontRenameCandidateState state, FontRenameCandidateReadRequirements requirements, CancellationToken cancellationToken)
        => _database.WriteAsync(
            async (connection, token) =>
            {
                SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
                await using var transactionScope = transaction.ConfigureAwait(false);
                var context = await ReadRenameCandidateContextAsync(connection, transaction, requirements, token).ConfigureAwait(false);
                ValidateRenameCandidateWrite(context, state, requirements);
                if (context.State?.OperationRequestId is long currentRequest && state.OperationRequestId != currentRequest
                    && (state.Status != FontRenameCandidateGenerationStatus.Pending || (state.OperationRequestId is null || state.OperationRequestId < currentRequest)))
                {
                    throw new InvalidOperationException("A newer rename candidate request has replaced this calculation.");
                }

                await WriteRenameCandidateStateAsync(connection, transaction, state, token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    /// <inheritdoc/>
    public Task SaveRenameCandidatesAsync(FontRenameCandidatePreparation preparation, FontRenameCandidateReadRequirements requirements, long? expectedRequestId, CancellationToken cancellationToken)
        => _database.WriteAsync(
            async (connection, token) =>
            {
                SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(token).ConfigureAwait(false);
                await using var transactionScope = transaction.ConfigureAwait(false);
                var context = await ReadRenameCandidateContextAsync(connection, transaction, requirements, token).ConfigureAwait(false);
                ValidateRenameCandidateWrite(context, preparation.State, requirements);
                if (context.State?.OperationRequestId != expectedRequestId || preparation.State.OperationRequestId != expectedRequestId)
                {
                    throw new InvalidOperationException("A newer rename candidate request has replaced this calculation.");
                }

                if (preparation.Items.Any(item => item.File.GenerationId != preparation.State.GenerationId))
                {
                    throw new InvalidOperationException("Rename candidate evidence belongs to a different generation.");
                }

                await ReplaceRenameCandidatesAsync(connection, transaction, preparation, token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    private static async Task<FontRenameCandidateReadContext> ReadRenameCandidateContextAsync(SqliteConnection connection, SqliteTransaction transaction, FontRenameCandidateReadRequirements requirements, CancellationToken token)
    {
        FontPublishedAnalysisContext? publication = null;
        FontRenameCandidateState? state = null;
        SqliteCommand command = connection.CreateCommand();
        await using var commandScope = command.ConfigureAwait(false);
        command.Transaction = transaction;
        command.CommandText = """
            SELECT g.id, b.id, b.evidence_version, g.input_json
            FROM schema_info s JOIN scan_generation g ON g.id = s.published_generation_id
            JOIN analysis_batch b ON b.generation_id = g.id
            WHERE b.status = $complete
            ORDER BY b.completed_utc DESC, b.id DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("$complete", (int)AnalysisBatchStatus.Complete);
        SqliteDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            if (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                publication = new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2), JsonSerializer.Deserialize<FontAnalysisInputSnapshot>(reader.GetString(3))!);
            }
        }

        command.CommandText = "SELECT state_json FROM rename_candidate_state WHERE singleton_id = 1;";
        object? saved = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
        if (saved is string json)
        {
            state = JsonSerializer.Deserialize<FontRenameCandidateState>(json);
        }

        return EvaluateRenameCandidateContext(publication, state, requirements);
    }

    internal static FontRenameCandidateReadContext EvaluateRenameCandidateContext(FontPublishedAnalysisContext? publication, FontRenameCandidateState? state, FontRenameCandidateReadRequirements requirements)
    {
        bool compatible = publication is not null && state is not null
            && publication.GenerationId == state.GenerationId && publication.AnalysisBatchId == state.AnalysisBatchId
            && publication.EvidenceVersion == requirements.AnalysisEvidenceVersion
            && publication.Input.PolicyFingerprint == requirements.PolicyFingerprint
            && publication.Input.EvidenceFingerprint == requirements.EvidenceFingerprint
            && state.RuleFingerprint == requirements.RuleFingerprint && state.RuleFingerprint == state.Rules.GetFingerprint()
            && state.CalculatorVersion == requirements.CalculatorVersion;
        if (state is not null && !compatible)
        {
            state = state with { Status = FontRenameCandidateGenerationStatus.Stale };
        }

        bool current = compatible && state!.Status == FontRenameCandidateGenerationStatus.Ready;
        string? reason = current ? null : publication is null ? "A complete current font analysis is required."
            : state is null ? "Rename candidates have not been generated."
            : state.Status == FontRenameCandidateGenerationStatus.Failed ? state.Error ?? "Rename candidate generation failed."
            : "Rename candidates are " + state.Status.ToString().ToLowerInvariant() + ".";
        return new(publication, state, current, reason);
    }

    internal static void ValidateRenameCandidateWrite(FontRenameCandidateReadContext context, FontRenameCandidateState state, FontRenameCandidateReadRequirements requirements)
    {
        if (!EvaluateRenameCandidateContext(context.Publication, state with { Status = FontRenameCandidateGenerationStatus.Ready }, requirements).IsCurrent)
        {
            throw new InvalidOperationException("The rename candidate publication or compatible inputs changed.");
        }
    }

    private static async Task WriteRenameCandidateStateAsync(SqliteConnection connection, SqliteTransaction transaction, FontRenameCandidateState state, CancellationToken token)
    {
        SqliteCommand command = connection.CreateCommand();
        await using var commandScope = command.ConfigureAwait(false);
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO rename_candidate_state(singleton_id, generation_id, analysis_batch_id, rule_fingerprint, status, operation_request_id, state_json)
            VALUES (1, $generation, $analysis, $rules, $status, $request, $json)
            ON CONFLICT(singleton_id) DO UPDATE SET generation_id = excluded.generation_id, analysis_batch_id = excluded.analysis_batch_id,
                rule_fingerprint = excluded.rule_fingerprint, status = excluded.status, operation_request_id = excluded.operation_request_id, state_json = excluded.state_json;
            """;
        command.Parameters.AddWithValue("$generation", state.GenerationId);
        command.Parameters.AddWithValue("$analysis", state.AnalysisBatchId);
        command.Parameters.AddWithValue("$rules", state.RuleFingerprint);
        command.Parameters.AddWithValue("$status", (int)state.Status);
        command.Parameters.AddWithValue("$request", (object?)state.OperationRequestId ?? DBNull.Value);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(state));
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    internal static void ValidatePublishedCandidatePreparation(AnalysisBatch batch, FontRenameCandidatePreparation preparation)
    {
        var state = preparation.State;
        if (state.GenerationId != batch.GenerationId || state.AnalysisBatchId != 0
            || state.OperationRequestId is not null || state.RuleFingerprint != state.Rules.GetFingerprint()
            || state.Status is not (FontRenameCandidateGenerationStatus.Ready or FontRenameCandidateGenerationStatus.Failed or FontRenameCandidateGenerationStatus.Stale)
            || (state.Status != FontRenameCandidateGenerationStatus.Ready && preparation.Items.Count != 0)
            || preparation.Items.Any(item => item.File.GenerationId != batch.GenerationId))
        {
            throw new InvalidOperationException("Rename candidate preparation does not belong to the new analysis.");
        }
    }

    private static async Task ReplaceRenameCandidatesAsync(SqliteConnection connection, SqliteTransaction transaction, FontRenameCandidatePreparation preparation, CancellationToken token)
    {
        await WriteRenameCandidateStateAsync(connection, transaction, preparation.State, token).ConfigureAwait(false);
        SqliteCommand command = connection.CreateCommand();
        await using var commandScope = command.ConfigureAwait(false);
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM rename_candidate;";
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        command.CommandText = """
            INSERT INTO rename_candidate(file_id, generation_id, physical_identity, source_key, old_path, new_path, source_path_key, target_path_key, category, skip_reason, item_json)
            SELECT $file, $generation, $identity, $key, $old, $new, $sourcePathKey, $targetPathKey, $category, $skipReason, $json
            WHERE EXISTS (SELECT 1 FROM font_file WHERE id = $file AND generation_id = $generation);
            """;
        foreach (var item in preparation.Items)
        {
            token.ThrowIfCancellationRequested();
            command.Parameters.Clear();
            command.Parameters.AddWithValue("$file", item.File.Id);
            command.Parameters.AddWithValue("$generation", item.File.GenerationId);
            command.Parameters.AddWithValue("$identity", item.File.PhysicalIdentity);
            command.Parameters.AddWithValue("$key", FontRenameStateIdentity.SourceKey(item.File.Path, item.NewPath ?? string.Empty, item.File));
            command.Parameters.AddWithValue("$old", item.File.Path);
            command.Parameters.AddWithValue("$new", item.NewPath ?? string.Empty);
            command.Parameters.AddWithValue("$sourcePathKey", FontRenameStateIdentity.PathKey(item.File.Path));
            command.Parameters.AddWithValue("$targetPathKey", FontRenameStateIdentity.PathKey(item.NewPath ?? string.Empty));
            command.Parameters.AddWithValue("$skipReason", (object?)item.SkipReason ?? DBNull.Value);
            command.Parameters.AddWithValue("$category", item.SkipReason is null ? "pending" : "skipped");
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(item));
            if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
            {
                throw new InvalidOperationException("Rename candidate evidence is not in the inventory generation.");
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1849:Call async methods when in an async method", Justification = "Microsoft.Data.Sqlite has no asynchronous deferred transaction overload; deferred reads retain WAL concurrency.")]
    private static SqliteTransaction BeginRenameReadTransaction(SqliteConnection connection) => connection.BeginTransaction(deferred: true);
}
