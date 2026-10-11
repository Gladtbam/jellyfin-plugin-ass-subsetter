using System.Collections.Generic;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Api;

/// <summary>Identifies selected durable maintenance items for restore, normalization, or rollback.</summary>
/// <param name="BatchId">The persisted maintenance or rename batch identifier.</param>
/// <param name="SelectedFileIds">The selected persisted physical-file identifiers.</param>
/// <param name="IdempotencyKey">The administrator-generated retry key.</param>
public sealed record QueueMaintenanceSelectionRequest(long BatchId, IReadOnlyList<long> SelectedFileIds, string IdempotencyKey);
