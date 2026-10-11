namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>Durable rename transition and safe-skip state.</summary>
public enum RenameItemStatus
{
    /// <summary>No movement has begun.</summary>
    Pending,

    /// <summary>A durable forward move intent exists.</summary>
    Moving,

    /// <summary>The verified case-hop file exists.</summary>
    Temporary,

    /// <summary>The file occupies its target name.</summary>
    Renamed,

    /// <summary>A durable rollback intent exists.</summary>
    RollingBack,

    /// <summary>The file occupies its original name.</summary>
    RolledBack,

    /// <summary>The destination is occupied and was not overwritten.</summary>
    DestinationOccupied,

    /// <summary>Current evidence differs from the immutable plan.</summary>
    EvidenceDrift,

    /// <summary>An active reader or writer excluded the file.</summary>
    InUse,

    /// <summary>Current policy excludes movement.</summary>
    Skipped,

    /// <summary>A filesystem operation failed.</summary>
    FailedIo,
}
