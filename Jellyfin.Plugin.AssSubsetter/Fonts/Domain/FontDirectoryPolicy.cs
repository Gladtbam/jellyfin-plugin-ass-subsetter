using System;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>
/// Directory policy flags persisted with an inventory file.
/// </summary>
[Flags]
public enum FontDirectoryPolicy
{
    /// <summary>No special directory policy applies.</summary>
    None = 0,

    /// <summary>The file is in a protected directory.</summary>
    Protected = 1,

    /// <summary>The file is in a preferred directory.</summary>
    Preferred = 2,

    /// <summary>The file is in a configured custom directory.</summary>
    Custom = 4
}
