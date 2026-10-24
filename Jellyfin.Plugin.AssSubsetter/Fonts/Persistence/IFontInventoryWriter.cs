using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <summary>Stages unpublished inventory generations without publishing them.</summary>
public interface IFontInventoryWriter
{
    /// <summary>Creates an unpublished inventory generation.</summary>
    /// <param name="input">The complete scan input.</param>
    /// <param name="changeReasons">Reasons for replacing the current publication.</param>
    /// <param name="cancellationToken">A token that cancels generation creation.</param>
    /// <returns>The new generation identifier.</returns>
    Task<long> BeginGenerationAsync(FontAnalysisInputSnapshot input, IReadOnlyList<string> changeReasons, CancellationToken cancellationToken);

    /// <summary>Writes inventory records into an unpublished generation.</summary>
    /// <param name="generationId">The unpublished generation identifier.</param>
    /// <param name="files">The physical font records to persist.</param>
    /// <param name="cancellationToken">A token that cancels the write.</param>
    /// <returns>A task representing the write.</returns>
    Task WriteInventoryAsync(long generationId, IReadOnlyList<FontFileRecord> files, CancellationToken cancellationToken);
}
