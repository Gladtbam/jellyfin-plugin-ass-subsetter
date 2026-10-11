namespace Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

/// <summary>Canonical inputs that determine the validity of a published analysis.</summary>
/// <param name="InventoryFingerprint">Observed files and stable Face evidence.</param>
/// <param name="PolicyFingerprint">Normalized directory policy.</param>
/// <param name="EvidenceFingerprint">Analysis compatibility key.</param>
public sealed record FontAnalysisInputSnapshot(string InventoryFingerprint, string PolicyFingerprint, string EvidenceFingerprint)
{
    /// <summary>Gets an absent input for explicitly constructed inventories.</summary>
    public static FontAnalysisInputSnapshot Empty { get; } = new(string.Empty, string.Empty, string.Empty);
}
