using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Microsoft.EntityFrameworkCore;
using static Jellyfin.Plugin.AssSubsetter.Fonts.Persistence.FontDatabaseRows;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <summary>Maintenance records and typed projections for durable item history.</summary>
public sealed partial class SqliteFontRepository
{
    /// <inheritdoc/>
    public async Task<MaintenanceBatch?> GetMaintenanceBatchAsync(long batchId, CancellationToken cancellationToken)
    {
        FontDbContext context = await _database.OpenContextAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = context.ConfigureAwait(false);
        var transaction = await BeginReadSnapshotAsync(context, cancellationToken).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);
        MaintenanceBatchRow? row = await context.Set<MaintenanceBatchRow>().SingleOrDefaultAsync(row => row.Id == batchId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : (await LoadMaintenanceRecordsAsync(context, [row], cancellationToken).ConfigureAwait(false))[0];
    }

    /// <inheritdoc/>
    public async Task<MaintenanceBatch?> GetMaintenanceBatchByOperationRequestAsync(long operationRequestId, CancellationToken cancellationToken)
    {
        FontDbContext context = await _database.OpenContextAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = context.ConfigureAwait(false);
        var transaction = await BeginReadSnapshotAsync(context, cancellationToken).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);
        MaintenanceBatchRow? row = await context.Set<MaintenanceBatchRow>().SingleOrDefaultAsync(row => row.OperationRequestId == operationRequestId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : (await LoadMaintenanceRecordsAsync(context, [row], cancellationToken).ConfigureAwait(false))[0];
    }

    /// <inheritdoc/>
    public async Task<FontRepositoryPage<MaintenanceBatch>> LoadMaintenanceBatchPageAsync(long offset, int pageSize, IReadOnlySet<FontOperationKind>? kinds, CancellationToken cancellationToken)
    {
        ValidatePage(offset, pageSize);
        if (kinds is { Count: 0 })
        {
            return new FontRepositoryPage<MaintenanceBatch>([], 0);
        }

        FontDbContext context = await _database.OpenContextAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = context.ConfigureAwait(false);
        var transaction = await BeginReadSnapshotAsync(context, cancellationToken).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);
        int[] kindValues = kinds?.Select(kind => (int)kind).ToArray() ?? [];
        IQueryable<MaintenanceBatchRow> query = context.Set<MaintenanceBatchRow>();
        if (kinds is not null)
        {
            query = query.Where(row => kindValues.Contains(row.OperationKind));
        }

        long count = await query.LongCountAsync(cancellationToken).ConfigureAwait(false);
        string kindsJson = JsonSerializer.Serialize(kindValues);
        int filter = kinds is null ? 0 : 1;
        List<MaintenanceBatchRow> rows = await context.Set<MaintenanceBatchRow>().FromSqlInterpolated($"""
            SELECT * FROM maintenance_batch
            WHERE ({filter} = 0 OR operation_kind IN (SELECT value FROM json_each({kindsJson})))
            ORDER BY COALESCE(completed_utc, started_utc) DESC, id DESC
            LIMIT {pageSize} OFFSET {offset}
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new FontRepositoryPage<MaintenanceBatch>(await LoadMaintenanceRecordsAsync(context, rows, cancellationToken).ConfigureAwait(false), count);
    }

    /// <inheritdoc/>
    public async Task<FontRepositoryPage<QuarantineItemRecord>> LoadQuarantineItemPageAsync(long offset, int pageSize, IReadOnlySet<QuarantineItemStatus> statuses, CancellationToken cancellationToken)
    {
        ValidatePage(offset, pageSize);
        ArgumentNullException.ThrowIfNull(statuses);
        if (statuses.Count == 0)
        {
            return new FontRepositoryPage<QuarantineItemRecord>([], 0);
        }

        FontDbContext context = await _database.OpenContextAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = context.ConfigureAwait(false);
        var transaction = await BeginReadSnapshotAsync(context, cancellationToken).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);
        int[] codes = statuses.Select(status => (int)status).ToArray();
        string json = JsonSerializer.Serialize(codes);
        int kind = (int)FontOperationKind.Quarantine;
        // Rank once: a correlated newer-row lookup would repeatedly scan unindexed source paths.
        List<long> counts = await context.Database.SqlQuery<long>($"""
            WITH ranked AS (
                SELECT qi.status, ROW_NUMBER() OVER (PARTITION BY qi.source_path ORDER BY qi.id DESC) AS source_rank
                FROM quarantine_item qi JOIN maintenance_batch b ON b.id = qi.maintenance_batch_id
                WHERE b.operation_kind = {kind}
            )
            SELECT COUNT(*) AS Value FROM ranked
            WHERE source_rank = 1 AND status IN (SELECT value FROM json_each({json}))
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
        long count = counts.Single();
        List<QuarantineItemRow> rows = await context.Set<QuarantineItemRow>().FromSqlInterpolated($"""
            WITH ranked AS (
                SELECT qi.*, ROW_NUMBER() OVER (PARTITION BY qi.source_path ORDER BY qi.id DESC) AS source_rank
                FROM quarantine_item qi JOIN maintenance_batch b ON b.id = qi.maintenance_batch_id
                WHERE b.operation_kind = {kind}
            )
            SELECT * FROM ranked WHERE source_rank = 1 AND status IN (SELECT value FROM json_each({json}))
            ORDER BY id DESC LIMIT {pageSize} OFFSET {offset}
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new FontRepositoryPage<QuarantineItemRecord>(rows.Select(ToQuarantineItem).ToArray(), count);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<RenameItemRecord>> LoadRenameStateItemsAsync(CancellationToken cancellationToken)
    {
        FontDbContext context = await _database.OpenContextAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = context.ConfigureAwait(false);
        var rows = await (
            from item in context.Set<RenameItemRow>()
            join batch in context.Set<MaintenanceBatchRow>() on item.MaintenanceBatchId equals batch.Id
            orderby batch.Id, item.FileId
            select new { item.ItemJson, BatchId = batch.Id, batch.OperationRequestId }).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(row => JsonSerializer.Deserialize<RenameItemRecord>(row.ItemJson)! with { MaintenanceBatchId = row.BatchId, OperationRequestId = row.OperationRequestId }).ToArray();
    }

    private static async Task<IReadOnlyList<MaintenanceBatch>> LoadMaintenanceRecordsAsync(FontDbContext context, List<MaintenanceBatchRow> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        long[] ids = rows.Select(row => row.Id).ToArray();
        List<QuarantineItemRow> items = await context.Set<QuarantineItemRow>().Where(row => ids.Contains(row.MaintenanceBatchId))
            .OrderBy(row => row.FileId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var renames = await context.Set<RenameItemRow>().Where(row => ids.Contains(row.MaintenanceBatchId)).OrderBy(row => row.FileId)
            .Select(row => new { row.MaintenanceBatchId, row.ItemJson }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var itemsByBatch = items.ToLookup(row => row.MaintenanceBatchId);
        var renamesByBatch = renames.ToLookup(row => row.MaintenanceBatchId);
        return rows.Select(row => new MaintenanceBatch
        {
            Id = row.Id, OperationRequestId = row.OperationRequestId, Kind = (FontOperationKind)row.OperationKind,
            Status = (MaintenanceBatchStatus)row.Status, StartedUtc = ParseDate(row.StartedUtc),
            CompletedUtc = ParseNullableDate(row.CompletedUtc), SummaryJson = row.SummaryJson,
            Items = itemsByBatch[row.Id].Select(ToQuarantineItem).ToArray(),
            RenameItems = renamesByBatch[row.Id].Select(item => JsonSerializer.Deserialize<RenameItemRecord>(item.ItemJson)!).ToArray()
        }).ToArray();
    }

    private static QuarantineItemRecord ToQuarantineItem(QuarantineItemRow row) => new()
    {
        Id = row.Id, MaintenanceBatchId = row.MaintenanceBatchId, FileId = row.FileId,
        SourcePath = row.SourcePath, StagingPath = row.StagingPath, QuarantinePath = row.QuarantinePath,
        Sha256 = row.Sha256, Size = row.Size, Status = (QuarantineItemStatus)row.Status,
        QuarantinedUtc = ParseNullableDate(row.QuarantinedUtc), ExpiresUtc = ParseNullableDate(row.ExpiresUtc),
        RestoredUtc = ParseNullableDate(row.RestoredUtc), Error = row.Error
    };
}
