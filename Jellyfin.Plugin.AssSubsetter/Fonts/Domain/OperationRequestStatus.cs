namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>
/// Durable operation request lifecycle state.
/// </summary>
public enum OperationRequestStatus
{
    /// <summary>The operation is waiting for its scheduled task.</summary>
    Pending,

    /// <summary>A scheduled task has claimed the operation.</summary>
    Running,

    /// <summary>The operation completed successfully.</summary>
    Complete,

    /// <summary>The operation stopped with an error.</summary>
    Failed,

    /// <summary>The operation was cancelled.</summary>
    Cancelled
}
