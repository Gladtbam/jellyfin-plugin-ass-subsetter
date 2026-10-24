using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <summary>Persists recoverable maintenance batches and per-file outcomes.</summary>
public interface IFontMaintenanceRepository : IFontMaintenanceReader
{
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
}
