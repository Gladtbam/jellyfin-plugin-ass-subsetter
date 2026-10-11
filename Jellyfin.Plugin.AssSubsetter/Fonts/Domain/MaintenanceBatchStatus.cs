namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>
/// Durable maintenance-batch lifecycle state.
/// </summary>
public enum MaintenanceBatchStatus
{
    /// <summary>The batch is running or resumable.</summary>
    Running,

    /// <summary>The batch completed successfully.</summary>
    Complete,

    /// <summary>The batch completed with one or more skipped or failed items.</summary>
    Partial,

    /// <summary>The batch stopped with an unrecoverable error.</summary>
    Failed,

    /// <summary>The batch was cancelled after stabilizing its current item.</summary>
    Cancelled
}
