using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <summary>Reads database health and local performance measurements.</summary>
public interface IFontDiagnosticsReader
{
    /// <summary>Returns an administrator-facing database health snapshot.</summary>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The current database health snapshot.</returns>
    Task<FontDatabaseHealth> GetDatabaseHealthAsync(CancellationToken cancellationToken);

    /// <summary>Loads the newest local performance samples.</summary>
    /// <param name="maximumCount">The maximum number of samples to return.</param>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The samples ordered newest first.</returns>
    Task<IReadOnlyList<PerformanceSample>> LoadRecentPerformanceSamplesAsync(int maximumCount, CancellationToken cancellationToken);
}
