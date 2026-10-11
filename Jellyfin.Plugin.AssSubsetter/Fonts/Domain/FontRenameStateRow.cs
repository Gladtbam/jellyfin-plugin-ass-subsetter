namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>A paged candidate or durable source-specific execution outcome.</summary>
/// <param name="Key">The stable row identity.</param>
/// <param name="MaintenanceBatchId">The historical execution batch, when allocated.</param>
/// <param name="OperationRequestId">The original immutable request, when accepted.</param>
/// <param name="GenerationId">The source publication generation.</param>
/// <param name="FileId">The physical source file ID.</param>
/// <param name="CanExecute">Whether new selection is available.</param>
/// <param name="CanRollback">Whether historical rollback may be submitted for validation.</param>
/// <param name="Item">Only this row's evidence and state.</param>
public sealed record FontRenameStateRow(string Key, long? MaintenanceBatchId, long? OperationRequestId, long GenerationId, long FileId, bool CanExecute, bool CanRollback, RenameItemRecord Item);
