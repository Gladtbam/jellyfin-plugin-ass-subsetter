using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Maintenance;

/// <summary>Provides a process-local manifest store for deterministic maintenance tests.</summary>
internal sealed class InMemoryQuarantineManifestStore : IQuarantineManifestStore
{
    private readonly object _sync = new();
    private readonly Dictionary<long, QuarantineManifest> _manifests = [];

    /// <summary>Writes or replaces the complete manifest for one batch.</summary>
    /// <param name="manifest">The complete manifest.</param>
    /// <param name="cancellationToken">A token that cancels before persistence.</param>
    /// <returns>A completed task.</returns>
    public Task SaveAsync(QuarantineManifest manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        cancellationToken.ThrowIfCancellationRequested();
        manifest.ValidateCurrentFormat();
        lock (_sync)
        {
            _manifests[manifest.BatchId] = manifest;
        }

        return Task.CompletedTask;
    }

    /// <summary>Loads the current manifest snapshots ordered by batch identifier.</summary>
    /// <param name="cancellationToken">A token that cancels enumeration.</param>
    /// <returns>The current manifest snapshots.</returns>
    public Task<IReadOnlyList<QuarantineManifest>> LoadAllAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            foreach (QuarantineManifest manifest in _manifests.Values)
            {
                manifest.ValidateCurrentFormat();
            }

            return Task.FromResult<IReadOnlyList<QuarantineManifest>>(_manifests.Values.OrderBy(manifest => manifest.BatchId).ToArray());
        }
    }
}
