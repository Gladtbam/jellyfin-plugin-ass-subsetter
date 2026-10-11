namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>The directory action recorded before any mutation.</summary>
public enum RenameDirectoryAction
{
    /// <summary>Create one target component.</summary>
    Create,

    /// <summary>Remove one empty directory without deleting its contents.</summary>
    RemoveEmpty
}
