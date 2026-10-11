using System.Collections.Generic;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Api;

/// <summary>One selectable source-specific management row.</summary>
/// <param name="Key">Stable source row identity.</param>
/// <param name="MaintenanceBatchId">Owning execution batch.</param>
/// <param name="OperationRequestId">Owning saved request.</param>
/// <param name="GenerationId">Source generation.</param>
/// <param name="FileId">Source physical identifier.</param>
/// <param name="CanExecute">Whether a new selection can be authorized.</param>
/// <param name="CanRollback">Whether the saved target still matches the physical file.</param>
/// <param name="Item">Evidence, paths and outcome.</param>
public sealed record RenameStateItemDto(string Key, long? MaintenanceBatchId, long? OperationRequestId, long GenerationId, long FileId, bool CanExecute, bool CanRollback, RenameItemDto Item);
