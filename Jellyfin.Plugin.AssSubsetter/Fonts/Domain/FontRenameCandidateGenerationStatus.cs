namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>Lifecycle of the current derived rename candidates.</summary>
public enum FontRenameCandidateGenerationStatus
{
    /// <summary>Waiting for calculation.</summary>
    Pending,

    /// <summary>Calculation is in progress.</summary>
    Running,

    /// <summary>A complete current result, including an empty result.</summary>
    Ready,

    /// <summary>The inputs no longer match.</summary>
    Stale,

    /// <summary>Calculation failed independently of font analysis.</summary>
    Failed
}
