using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

/// <summary>Provides the filename-normalization seam that prioritizes explicit requests and, when none is pending, can create and execute a request for the current candidates.</summary>
public interface IFontFilenameNormalizationExecutor
{
    /// <summary>Reconciles durable filename intents after restart or database replacement.</summary>
    /// <param name="cancellationToken">Cancels recovery between batches.</param>
    /// <returns>A task representing recovery.</returns>
    Task ReconcileAsync(CancellationToken cancellationToken);

    /// <summary>Executes an authorized rollback of selected completed rename items.</summary>
    /// <param name="request">The persisted rollback authorization.</param>
    /// <param name="cancellationToken">Cancels between file transitions.</param>
    /// <param name="progress">Optional progress across files and finalization.</param>
    /// <returns>The durable rollback batch.</returns>
    Task<MaintenanceBatch> RollbackAsync(OperationRequest request, CancellationToken cancellationToken, IProgress<double>? progress = null);

    /// <summary>Creates and persists a filename-normalization request for the current candidates.</summary>
    /// <param name="cancellationToken">A token that cancels candidate discovery or request persistence.</param>
    /// <returns>The persisted pending request, or <see langword="null"/> when no eligible candidate is available.</returns>
    Task<OperationRequest?> CreateCurrentRequestAsync(CancellationToken cancellationToken);

    /// <summary>Executes one already claimed immutable request.</summary>
    /// <param name="request">The claimed filename-normalization request.</param>
    /// <param name="cancellationToken">A token that cancels before the next file transition.</param>
    /// <param name="progress">Optional progress across files and finalization.</param>
    /// <returns>The durable maintenance batch.</returns>
    Task<MaintenanceBatch> ExecuteAsync(OperationRequest request, CancellationToken cancellationToken, IProgress<double>? progress = null);
}
