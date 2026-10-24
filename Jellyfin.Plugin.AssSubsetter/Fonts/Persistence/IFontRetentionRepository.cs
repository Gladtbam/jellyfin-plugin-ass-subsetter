using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <summary>Prunes obsolete inventory and analysis while preserving active work.</summary>
public interface IFontRetentionRepository
{
    /// <summary>Deletes superseded inventory and abandoned staged inventory that have no retained report or rename plan.</summary>
    /// <param name="publishedGenerationRetentionCount">Number of recent published generations to keep; zero retains them all.</param>
    /// <param name="abandonedBeforeUtc">Building generations created before this UTC instant can be removed.</param>
    /// <param name="progress">Reports completed generation deletions from zero through one hundred.</param>
    /// <param name="cancellationToken">Cancels pruning between generations or during a deletion.</param>
    /// <returns>The number of inventory generations removed.</returns>
    Task<int> PruneObsoleteGenerationsAsync(int publishedGenerationRetentionCount, DateTimeOffset abandonedBeforeUtc, IProgress<double>? progress, CancellationToken cancellationToken);

    /// <summary>Retains the newest completed analysis batches and batches required by active work.</summary>
    /// <param name="retentionCount">Number of newest results to retain; zero retains all results.</param>
    /// <param name="cancellationToken">A token that cancels pruning.</param>
    /// <returns>The number of reports deleted.</returns>
    Task<int> PruneAnalysisBatchesAsync(int retentionCount, CancellationToken cancellationToken);
}
