namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>
/// Administrator or scheduler font operation kind.
/// </summary>
public enum FontOperationKind
{
    /// <summary>Refresh inventory and analyze duplicates.</summary>
    Analyze,

    /// <summary>Move verified duplicate files into quarantine.</summary>
    Quarantine,

    /// <summary>Restore quarantined files.</summary>
    Restore,

    /// <summary>Permanently purge expired quarantine files.</summary>
    Purge,

    /// <summary>Normalize eligible font file names.</summary>
    NormalizeNames,

    /// <summary>Roll back a completed rename batch.</summary>
    RollbackNames,

    /// <summary>Rebuild derived database content.</summary>
    RebuildDerivedData,

    /// <summary>Regenerate derived filename candidates without publishing analysis.</summary>
    RefreshRenameCandidates = 7
}
