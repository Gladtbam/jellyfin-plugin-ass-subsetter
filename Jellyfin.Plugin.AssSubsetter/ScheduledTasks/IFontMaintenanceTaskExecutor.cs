using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

namespace Jellyfin.Plugin.AssSubsetter.ScheduledTasks;

/// <summary>Coordinates scheduled font maintenance and operations requested through font management.</summary>
public interface IFontMaintenanceTaskExecutor
{
    /// <summary>Gets a value indicating whether any font task holds the shared execution slot.</summary>
    bool IsBusy { get; }

    /// <summary>Runs the enabled stages and finishes with catalog optimization under one shared execution slot.</summary>
    /// <param name="stages">The stages enabled for this run.</param>
    /// <param name="retentionCount">The analysis history retention count. Zero retains all history.</param>
    /// <param name="progress">Receives progress across the enabled stages and catalog optimization.</param>
    /// <param name="cancellationToken">Cancels the recovery wait or the active stage.</param>
    /// <returns>The last stage result or the reason the run stopped before a stage began.</returns>
    Task<FontTaskExecutionResult> RunScheduledMaintenanceAsync(FontMaintenanceStages stages, int retentionCount, IProgress<double> progress, CancellationToken cancellationToken);

    /// <summary>Consumes existing durable authorization without synthesizing scheduled work.</summary>
    /// <param name="kind">The pending operation kind.</param>
    /// <param name="progress">Receives execution progress.</param>
    /// <param name="cancellationToken">Cancels background execution.</param>
    /// <returns>The consumed operation, or a deferred result while another task is active or its authorization was already claimed.</returns>
    Task<FontTaskExecutionResult> ExecutePendingOperationAsync(FontOperationKind kind, IProgress<double> progress, CancellationToken cancellationToken);

    /// <summary>Consumes at most one explicit restore request.</summary>
    /// <param name="cancellationToken">A token that cancels before or during restore.</param>
    /// <returns>The maintenance batch or safe-skip summary.</returns>
    Task<FontTaskExecutionResult> RestoreQuarantinedFontsAsync(CancellationToken cancellationToken);
}
