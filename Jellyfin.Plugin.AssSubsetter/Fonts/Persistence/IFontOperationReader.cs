using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <summary>Reads durable operation identities and completed scan outcomes.</summary>
public interface IFontOperationReader
{
    /// <summary>Loads the latest successful analysis operation with its scan outcome.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The latest completed operation, if any.</returns>
    Task<OperationRequest?> GetLatestCompletedAnalysisOperationAsync(CancellationToken cancellationToken);

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
}
