namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>
/// Health state established while inspecting a physical font file.
/// </summary>
public enum FontFileHealth
{
    /// <summary>The file has not been inspected yet.</summary>
    Unknown,

    /// <summary>The file was parsed and accepted.</summary>
    Healthy,

    /// <summary>The file could not be classified safely.</summary>
    Unclassifiable
}
