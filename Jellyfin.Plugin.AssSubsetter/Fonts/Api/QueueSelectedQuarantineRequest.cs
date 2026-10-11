using System.Collections.Generic;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Api;

/// <summary>Identifies selected persisted files for an explicit analysis or maintenance batch.</summary>
/// <param name="BatchId">The persisted source batch identifier.</param>
/// <param name="SelectedFileIds">The selected persisted physical-file identifiers.</param>
/// <param name="IdempotencyKey">The administrator-generated retry key.</param>
public sealed record QueueSelectedQuarantineRequest(long BatchId, IReadOnlyList<long> SelectedFileIds, string IdempotencyKey);
