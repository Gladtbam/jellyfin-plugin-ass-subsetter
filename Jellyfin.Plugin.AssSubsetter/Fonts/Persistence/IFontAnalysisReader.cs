using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <summary>Reads completed analysis evidence and immutable reports.</summary>
public interface IFontAnalysisReader
{
    /// <summary>Checks whether a generation has a durably completed duplicate analysis.</summary>
    /// <param name="generationId">The staged generation to check.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns><see langword="true"/> only when a complete analysis batch exists.</returns>
    Task<bool> HasCompleteAnalysisAsync(long generationId, CancellationToken cancellationToken);

    /// <summary>Returns the newest completed duplicate-analysis report.</summary>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The newest complete report, or <see langword="null"/>.</returns>
    Task<AnalysisBatch?> GetLatestCompleteAnalysisAsync(CancellationToken cancellationToken);

    /// <summary>Loads one immutable duplicate-analysis report by identifier.</summary>
    /// <param name="batchId">The persisted analysis batch identifier.</param>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The report, or <see langword="null"/> when it does not exist.</returns>
    Task<AnalysisBatch?> GetAnalysisBatchAsync(long batchId, CancellationToken cancellationToken);

    /// <summary>Loads one deterministic administrator page of immutable analysis reports.</summary>
    /// <param name="offset">The zero-based row offset.</param>
    /// <param name="pageSize">The bounded number of reports to load.</param>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The page and exact total matching report count.</returns>
    Task<FontRepositoryPage<AnalysisBatch>> LoadAnalysisBatchPageAsync(long offset, int pageSize, CancellationToken cancellationToken);
}
