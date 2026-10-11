using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Api;

/// <summary>Describes administrator-only filename preview or execution state.</summary>
/// <param name="FileId">The immutable source file identifier.</param>
/// <param name="OldPath">The original path rendered as text.</param>
/// <param name="NewPath">The generated target path rendered as text.</param>
/// <param name="Sha256">The immutable source hash for administrator audit.</param>
/// <param name="SourceRoot">The owning custom physical root.</param>
/// <param name="NamingEvidence">The fields and branches actually used by the saved rules.</param>
/// <param name="CollisionFingerprint">The fingerprint suffix used to resolve a remaining collision.</param>
/// <param name="Status">The rename lifecycle state.</param>
/// <param name="Error">The skip or error reason.</param>
public sealed record RenameItemDto(long FileId, string OldPath, string? NewPath, string Sha256, string? SourceRoot, FontNamingEvidence NamingEvidence, string? CollisionFingerprint, RenameItemStatus Status, string? Error)
{
    /// <summary>Gets the saved font inspection error, separate from the rename operation outcome.</summary>
    public string? InspectionError { get; init; }
}
