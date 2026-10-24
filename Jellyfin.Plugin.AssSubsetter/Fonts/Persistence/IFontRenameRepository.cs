using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <summary>Queues candidate refreshes and persists candidate state against publication identity.</summary>
public interface IFontRenameRepository : IFontRenameReader, IFontOperationReader
{
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
}
