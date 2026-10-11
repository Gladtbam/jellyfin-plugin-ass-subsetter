namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>
/// Persisted relationship evidence for one unordered Face pair.
/// </summary>
public sealed record FontRelationRecord
{
    /// <summary>Gets the persisted relation identifier.</summary>
    public long Id { get; init; }

    /// <summary>Gets the lower Face identifier.</summary>
    public long LeftFaceId { get; init; }

    /// <summary>Gets the higher Face identifier.</summary>
    public long RightFaceId { get; init; }

    /// <summary>Gets the first matching relationship kind.</summary>
    public FontRelationshipKind RelationshipKind { get; init; }

    /// <summary>Gets the direct replacement direction.</summary>
    public FontReplacementDirection ReplacementDirection { get; init; }

    /// <summary>Gets the evidence schema version.</summary>
    public int EvidenceVersion { get; init; }

    /// <summary>Gets the complete persisted evidence document.</summary>
    public required string EvidenceJson { get; init; }
}
