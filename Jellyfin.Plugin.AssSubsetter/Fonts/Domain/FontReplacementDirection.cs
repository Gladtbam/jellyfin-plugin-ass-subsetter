namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>
/// Directional replacement conclusion attached to a pair relationship.
/// </summary>
public enum FontReplacementDirection
{
    /// <summary>Neither Face is a proven direct replacement.</summary>
    None,

    /// <summary>The left Face directly replaces the right Face.</summary>
    LeftReplacesRight,

    /// <summary>The right Face directly replaces the left Face.</summary>
    RightReplacesLeft,

    /// <summary>Both Faces are proven direct replacements for each other.</summary>
    Both
}
