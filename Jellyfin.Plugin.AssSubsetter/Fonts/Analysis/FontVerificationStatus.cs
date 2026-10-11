namespace Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;

/// <summary>Distinguishes verified success, observed failure, and unavailable proof.</summary>
public enum FontVerificationStatus
{
    /// <summary>No complete verification was performed.</summary>
    Unknown,

    /// <summary>The verification completed successfully.</summary>
    Passed,

    /// <summary>The verification observed invalid evidence or unsuccessful execution.</summary>
    Failed,

    /// <summary>The font does not declare the optional capability being verified.</summary>
    NotApplicable
}
