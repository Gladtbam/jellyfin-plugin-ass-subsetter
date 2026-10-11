using System.Collections.Generic;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

/// <summary>Contains the path-free immutable selection stored in an operation request.</summary>
/// <param name="BatchId">The source analysis or maintenance batch identifier.</param>
/// <param name="SelectedFileIds">The sorted distinct persisted physical-file identifiers.</param>
public sealed record FontOperationRequestPayload(long BatchId, IReadOnlyList<long> SelectedFileIds);
