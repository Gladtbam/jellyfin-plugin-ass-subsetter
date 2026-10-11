namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>
/// Link behavior observed for a font path.
/// </summary>
public enum FontLinkKind
{
    /// <summary>A regular file path.</summary>
    None,

    /// <summary>An additional hard-link path to a known physical file.</summary>
    HardLinkAlias,

    /// <summary>A symbolic link or platform reparse point.</summary>
    SymbolicOrReparsePoint
}
