namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>A selected name record or file-level field.</summary>
/// <param name="Name">The exact text field.</param>
/// <param name="NameId">The OpenType name identifier, if applicable.</param>
/// <param name="FaceIndex">The Face index, or null for file-level aggregation.</param>
/// <param name="Source">The value provenance.</param>
/// <param name="Value">The value, or null when missing.</param>
public sealed record FontNamingFieldEvidence(string Name, int? NameId, int? FaceIndex, string Source, string? Value)
{
    /// <summary>Gets the specific unavailable-value reason, or null for available or intentionally empty text.</summary>
    public string? MissingReason { get; init; }
}
