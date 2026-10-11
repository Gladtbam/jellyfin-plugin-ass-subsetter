namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>Durable per-file quarantine lifecycle state.</summary>
public enum QuarantineItemStatus
{
    /// <summary>The item has not started filesystem mutation.</summary>
    Pending,

    /// <summary>A verified cross-volume staging copy exists.</summary>
    Staged,

    /// <summary>The source was removed after a durable quarantine copy was established.</summary>
    Quarantined,

    /// <summary>The item was restored to its original path.</summary>
    Restored,

    /// <summary>The expired quarantine copy was permanently removed.</summary>
    Purged,

    /// <summary>Current file evidence no longer matches the immutable request.</summary>
    EvidenceDrift,

    /// <summary>A required retained replacement is missing or changed.</summary>
    ReplacementMissing,

    /// <summary>A restore destination is occupied and was not overwritten.</summary>
    DestinationOccupied,

    /// <summary>A copied or quarantined file failed size or hash verification.</summary>
    FailedVerification,

    /// <summary>A filesystem operation failed.</summary>
    FailedIo,

    /// <summary>Cancellation stabilized this item before later work stopped.</summary>
    Cancelled,

    /// <summary>A durable restore intent exists and reconciliation must prefer restoring the source.</summary>
    Restoring,

    /// <summary>A verified final quarantine copy exists and source removal remains pending.</summary>
    Promoted
}
