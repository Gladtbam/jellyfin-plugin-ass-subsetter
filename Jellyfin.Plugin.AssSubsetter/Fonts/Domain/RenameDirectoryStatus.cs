namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>The durable directory outcome independent of file status.</summary>
public enum RenameDirectoryStatus
{
    /// <summary>Intent persisted before mutation.</summary>
    Planned,

    /// <summary>The action completed.</summary>
    Completed,

    /// <summary>Safety or content requires retention.</summary>
    Retained,

    /// <summary>A filesystem operation failed.</summary>
    Warning
}
