using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <summary>Reads durable maintenance batches and quarantine history.</summary>
public interface IFontMaintenanceReader
{
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
}
