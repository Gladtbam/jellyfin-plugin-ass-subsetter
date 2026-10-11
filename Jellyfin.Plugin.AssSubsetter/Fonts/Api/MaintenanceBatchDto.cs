using System;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Api;

/// <summary>Describes a durable maintenance batch without its raw summary document.</summary>
/// <param name="Id">The persisted maintenance batch identifier.</param>
/// <param name="OperationRequestId">The owning immutable operation request identifier.</param>
/// <param name="Kind">The maintenance operation kind.</param>
/// <param name="Status">The batch lifecycle state.</param>
/// <param name="StartedUtc">The batch start time.</param>
/// <param name="CompletedUtc">The terminal completion time.</param>
/// <param name="ItemCount">The persisted item count.</param>
public sealed record MaintenanceBatchDto(long Id, long OperationRequestId, FontOperationKind Kind, MaintenanceBatchStatus Status, DateTimeOffset StartedUtc, DateTimeOffset? CompletedUtc, int ItemCount);
