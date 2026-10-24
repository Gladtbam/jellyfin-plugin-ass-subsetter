using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <summary>Publishes analysis atomically with prepared rename candidates, or invalidates superseded reports.</summary>
public interface IFontAnalysisPublisher
{
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
}
