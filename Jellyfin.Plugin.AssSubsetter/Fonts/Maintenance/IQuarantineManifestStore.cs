using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

/// <summary>Persists immutable recovery manifests beside quarantine contents.</summary>
public interface IQuarantineManifestStore
{
    /// <summary>Writes the complete current manifest atomically.</summary>
    /// <param name="manifest">The complete manifest document.</param>
    /// <param name="cancellationToken">A token that cancels before replacement.</param>
    /// <returns>A task representing durable persistence.</returns>
    Task SaveAsync(QuarantineManifest manifest, CancellationToken cancellationToken);

    /// <summary>Loads every valid durable quarantine manifest.</summary>
    /// <param name="cancellationToken">A token that cancels enumeration.</param>
    /// <returns>The valid manifests ordered by batch identifier.</returns>
    Task<IReadOnlyList<QuarantineManifest>> LoadAllAsync(CancellationToken cancellationToken);
}
