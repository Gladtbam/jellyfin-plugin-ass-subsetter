namespace Jellyfin.Plugin.AssSubsetter.Fonts.Api;

/// <summary>Identifies the administrator-facing action category for one analysis report row.</summary>
public enum FontReportAction
{
    /// <summary>The reported file or relationship is retained automatically.</summary>
    Retain,

    /// <summary>The relationship contains a verified physical-file quarantine candidate.</summary>
    QuarantineCandidate,

    /// <summary>The relationship is diagnostic and requires no automatic action.</summary>
    ReportOnly,

    /// <summary>The relationship is a conflict that prevents automatic action.</summary>
    Conflict
}
