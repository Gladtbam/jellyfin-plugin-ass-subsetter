using System;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <summary>Describes a maintenance batch and its exact persisted item count.</summary>
/// <param name="Id">The persisted batch identifier.</param>
/// <param name="OperationRequestId">The owning operation request identifier.</param>
/// <param name="Kind">The maintenance operation kind.</param>
/// <param name="Status">The batch lifecycle state.</param>
/// <param name="StartedUtc">The batch start time.</param>
/// <param name="CompletedUtc">The completion time for terminal batches.</param>
/// <param name="ItemCount">The total number of persisted rename and quarantine items.</param>
public sealed record MaintenanceBatchSummary(
    long Id,
    long OperationRequestId,
    FontOperationKind Kind,
    MaintenanceBatchStatus Status,
    DateTimeOffset StartedUtc,
    DateTimeOffset? CompletedUtc,
    int ItemCount);
