using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <summary>Creates, claims and updates durable operation authorizations.</summary>
public interface IFontOperationRepository : IFontOperationReader
{
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
}
