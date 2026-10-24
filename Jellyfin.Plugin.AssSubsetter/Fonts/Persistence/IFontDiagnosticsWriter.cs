using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <summary>Records local inventory and analysis performance measurements.</summary>
public interface IFontDiagnosticsWriter
{
    /// <summary>Persists one local inventory or analysis performance sample.</summary>
    /// <param name="sample">The measured phase sample.</param>
    /// <param name="cancellationToken">A token that cancels persistence.</param>
    /// <returns>A task representing the write.</returns>
    Task SavePerformanceSampleAsync(PerformanceSample sample, CancellationToken cancellationToken);
}
