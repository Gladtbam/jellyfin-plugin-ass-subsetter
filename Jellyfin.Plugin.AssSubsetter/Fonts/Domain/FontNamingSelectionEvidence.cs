namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>A name-pair selection or collection aggregation.</summary>
/// <param name="FaceIndex">The selected Face, or null for file aggregation.</param>
/// <param name="Kind">The stable selection identifier.</param>
/// <param name="Detail">Additional evidence explaining the selection.</param>
public sealed record FontNamingSelectionEvidence(int? FaceIndex, string Kind, string? Detail);
