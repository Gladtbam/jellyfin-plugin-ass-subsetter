using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <summary>Reads staged and published inventory snapshots and their analysis inputs.</summary>
public interface IFontInventoryReader
{
    /// <summary>Gets the inputs associated with the current publication.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The published inputs, or null before initialization.</returns>
    Task<FontAnalysisInputSnapshot?> GetPublishedAnalysisInputAsync(CancellationToken cancellationToken);

    /// <summary>Loads file and Face metadata from the currently published generation without large Unicode coverage arrays.</summary>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The published physical-file inventory.</returns>
    Task<IReadOnlyList<FontFileRecord>> LoadPublishedInventoryAsync(CancellationToken cancellationToken);

    /// <summary>Loads physical-file identity and generation evidence for the requested paths in the current publication.</summary>
    /// <param name="paths">The inventory primary paths to match with the host's path comparison.</param>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The evidence for matching inventory files from one published read snapshot.</returns>
    Task<IReadOnlyList<(string Path, string PhysicalIdentity, long GenerationId)>> LoadPublishedFileEvidenceByPathAsync(
        IReadOnlyCollection<string> paths, CancellationToken cancellationToken);

    /// <summary>Loads complete published file and Face evidence, including Unicode coverage needed for scan reuse.</summary>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The published inventory for a new staged generation.</returns>
    Task<IReadOnlyList<FontFileRecord>> LoadPublishedInventoryForReuseAsync(CancellationToken cancellationToken);

    /// <summary>Loads complete file and Face evidence for one persisted inventory generation.</summary>
    /// <param name="generationId">The persisted generation identifier.</param>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The generation's physical-file inventory.</returns>
    Task<IReadOnlyList<FontFileRecord>> LoadInventoryAsync(long generationId, CancellationToken cancellationToken);
}
