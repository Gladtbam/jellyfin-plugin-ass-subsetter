using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

namespace Jellyfin.Plugin.AssSubsetter.ScheduledTasks;

/// <summary>Coordinates the durable work behind the five discoverable Jellyfin font tasks.</summary>
public interface IFontMaintenanceTaskExecutor
{
    /// <summary>Gets a value indicating whether any font task holds the shared execution slot.</summary>
    bool IsBusy { get; }

    /// <summary>Analyzes inventory and then prunes history within one execution slot.</summary>
    /// <param name="retentionCount">Number of reports and generations to retain; zero retains all.</param>
    /// <param name="progress">Receives combined task progress.</param>
    /// <param name="cancellationToken">Cancels either stage.</param>
    /// <returns>The primary analysis result.</returns>
    Task<FontTaskExecutionResult> RefreshAnalyzeAndPruneAsync(int retentionCount, IProgress<double> progress, CancellationToken cancellationToken);

    /// <summary>Quarantines candidates and then safely purges expired copies within one execution slot.</summary>
    /// <param name="progress">Receives combined task progress.</param>
    /// <param name="cancellationToken">Cancels either stage.</param>
    /// <returns>The primary quarantine result.</returns>
    Task<FontTaskExecutionResult> QuarantineAndPurgeAsync(IProgress<double> progress, CancellationToken cancellationToken);

    /// <summary>Consumes existing durable authorization without synthesizing scheduled work.</summary>
    /// <param name="kind">The pending operation kind.</param>
    /// <param name="progress">Receives execution progress.</param>
    /// <param name="cancellationToken">Cancels background execution.</param>
    /// <returns>The consumed operation, or a deferred result while another task is active or its authorization was already claimed.</returns>
    Task<FontTaskExecutionResult> ExecutePendingOperationAsync(FontOperationKind kind, IProgress<double> progress, CancellationToken cancellationToken);

    /// <summary>Prunes old reports and unreferenced inventory while no analysis or file maintenance is active.</summary>
    /// <param name="retentionCount">Number of analysis reports and published inventory generations to keep; zero keeps both.</param>
    /// <param name="progress">Receives cleanup progress from zero through one hundred.</param>
    /// <param name="cancellationToken">Cancels pruning.</param>
    /// <returns>Counts of removed data, or a skipped result when another maintenance task is active.</returns>
    Task<FontHistoryPruneResult> PruneHistoryAsync(int retentionCount, IProgress<double> progress, CancellationToken cancellationToken);

    /// <summary>Refreshes the published inventory and analyzes that exact generation.</summary>
    /// <param name="progress">Receives percentage completion from zero through one hundred.</param>
    /// <param name="cancellationToken">A token that cancels refresh or analysis.</param>
    /// <returns>The persisted analysis batch summary.</returns>
    Task<FontTaskExecutionResult> RefreshInventoryAndAnalyzeAsync(
        IProgress<double> progress,
        CancellationToken cancellationToken);

    /// <summary>Consumes one explicit request or synthesizes one request from the latest current complete analysis.</summary>
    /// <param name="progress">Receives per-file quarantine progress.</param>
    /// <param name="cancellationToken">A token that cancels before or during quarantine.</param>
    /// <returns>The maintenance batch or safe-skip summary.</returns>
    Task<FontTaskExecutionResult> QuarantineDuplicatesAsync(IProgress<double> progress, CancellationToken cancellationToken);

    /// <summary>Consumes at most one explicit restore request.</summary>
    /// <param name="cancellationToken">A token that cancels before or during restore.</param>
    /// <returns>The maintenance batch or safe-skip summary.</returns>
    Task<FontTaskExecutionResult> RestoreQuarantinedFontsAsync(CancellationToken cancellationToken);

    /// <summary>Purges quarantine items only through the fixed expiry verification policy.</summary>
    /// <param name="progress">Receives verified-item progress.</param>
    /// <param name="cancellationToken">A token that cancels before the next purge item.</param>
    /// <returns>The compact purge summary.</returns>
    Task<FontTaskExecutionResult> PurgeExpiredQuarantineAsync(IProgress<double> progress, CancellationToken cancellationToken);

    /// <summary>Consumes at most one explicit filename-normalization request through the filename normalizer.</summary>
    /// <param name="progress">Receives completed-file and finalization progress.</param>
    /// <param name="cancellationToken">A token that cancels before or during normalization.</param>
    /// <returns>The maintenance batch or safe-skip summary.</returns>
    Task<FontTaskExecutionResult> NormalizeFilenamesAsync(IProgress<double> progress, CancellationToken cancellationToken);
}
