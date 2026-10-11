using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

/// <summary>Reconciles durable rename evidence before normal host operation begins.</summary>
public sealed class FontRenameRecoveryService : IHostedService
{
    private readonly IFontRepository _repository;
    private readonly IFontFilenameNormalizationExecutor _normalizer;

    /// <summary>Initializes a new instance of the <see cref="FontRenameRecoveryService"/> class.</summary>
    /// <param name="repository">Initializes or recovers the fixed SQLite catalog.</param>
    /// <param name="normalizer">Reimports and reconciles immutable rename intents.</param>
    public FontRenameRecoveryService(IFontRepository repository, IFontFilenameNormalizationExecutor normalizer)
    {
        _repository = repository;
        _normalizer = normalizer;
    }

    /// <summary>Reconciles filesystem state and persists durable follow-up analysis for later scheduling.</summary>
    /// <param name="cancellationToken">Cancels host startup recovery.</param>
    /// <returns>A task representing startup recovery.</returns>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _normalizer.ReconcileAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Completes shutdown after startup-only recovery.</summary>
    /// <param name="cancellationToken">The host shutdown token.</param>
    /// <returns>A completed task.</returns>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
