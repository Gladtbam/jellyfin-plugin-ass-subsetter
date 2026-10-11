namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>
/// Lifecycle state of an immutable duplicate-analysis report.
/// </summary>
public enum AnalysisBatchStatus
{
    /// <summary>The analysis is still being built.</summary>
    Running,

    /// <summary>The analysis completed and is queryable.</summary>
    Complete,

    /// <summary>A newer inventory generation invalidated the analysis.</summary>
    Stale,

    /// <summary>The analysis stopped with a recorded error.</summary>
    Failed
}
