using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <summary>Reads immutable runtime face snapshots.</summary>
public interface IFontRuntimeFaceReader
{
    /// <summary>Loads runtime Faces from the currently published generation.</summary>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The immutable runtime Face snapshot.</returns>
    Task<IReadOnlyList<RuntimeFontFace>> LoadPublishedRuntimeFacesAsync(CancellationToken cancellationToken);

    /// <summary>Loads runtime Faces for a staged or published generation before publication.</summary>
    /// <param name="generationId">The generation to prepare for publication.</param>
    /// <param name="cancellationToken">Cancels the snapshot query.</param>
    /// <returns>The generation's immutable runtime Face snapshot.</returns>
    Task<IReadOnlyList<RuntimeFontFace>> LoadRuntimeFacesAsync(long generationId, CancellationToken cancellationToken);
}
