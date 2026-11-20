using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AssSubsetter.Services;

/// <summary>Shares the result of startup catalog recovery with font management and maintenance.</summary>
public sealed class FontCatalogReadiness
{
    private readonly TaskCompletionSource<bool> _recovered = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Waits for the startup service to report the catalog recovery result after its snapshot load attempt.</summary>
    /// <param name="cancellationToken">Cancels the caller's wait.</param>
    /// <returns>Whether catalog initialization and maintenance recovery succeeded.</returns>
    public Task<bool> WaitAsync(CancellationToken cancellationToken) => _recovered.Task.WaitAsync(cancellationToken);

    internal void Complete(bool recovered) => _recovered.TrySetResult(recovered);
}
