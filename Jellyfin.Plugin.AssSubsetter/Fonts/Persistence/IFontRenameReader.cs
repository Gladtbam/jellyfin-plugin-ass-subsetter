using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <summary>Reads rename authorizations, outcomes and candidate snapshots.</summary>
public interface IFontRenameReader
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

    /// <summary>Reads one status page, count and authorization context under the same snapshot.</summary>
    /// <param name="status">The status category.</param>
    /// <param name="page">The one-based page.</param>
    /// <param name="pageSize">The bounded page size.</param>
    /// <param name="requirements">Current compatible inputs.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>Only the requested rows and consistent metadata.</returns>
    Task<FontRenameStatePage> LoadRenameStatePageAsync(string status, int page, int pageSize, FontRenameCandidateReadRequirements requirements, CancellationToken cancellationToken);
}
