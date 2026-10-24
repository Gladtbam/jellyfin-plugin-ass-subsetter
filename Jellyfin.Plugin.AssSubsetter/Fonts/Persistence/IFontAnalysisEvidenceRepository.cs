using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <summary>Reads inventory evidence and caches reusable deep analysis results.</summary>
public interface IFontAnalysisEvidenceRepository : IFontInventoryReader
{
    /// <summary>Loads reusable complete deep evidence for an unchanged physical file.</summary>
    /// <param name="file">The current generation's file identity and inventory fingerprint.</param>
    /// <param name="evidenceKey">The parser and native-environment compatibility key.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The stored payload, or <see langword="null"/> when no compatible evidence exists.</returns>
    Task<string?> LoadReusableDeepEvidenceAsync(FontFileRecord file, string evidenceKey, CancellationToken cancellationToken);

    /// <summary>Persists complete deep evidence for one physical file in the current generation.</summary>
    /// <param name="fileId">The current persisted file identifier.</param>
    /// <param name="evidenceKey">The parser and native-environment compatibility key.</param>
    /// <param name="payload">The complete serialized Face evidence.</param>
    /// <param name="cancellationToken">Cancels persistence.</param>
    /// <returns>A task representing persistence.</returns>
    Task SaveDeepEvidenceAsync(long fileId, string evidenceKey, string payload, CancellationToken cancellationToken);
}
