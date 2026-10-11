namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>Describes one persisted member of a direct duplicate group.</summary>
public sealed record DuplicateGroupMemberRecord
{
    /// <summary>Gets the persisted member identifier.</summary>
    public long Id { get; init; }

    /// <summary>Gets the persisted Face identifier.</summary>
    public long FaceId { get; init; }

    /// <summary>Gets the containing physical-file identifier.</summary>
    public long FileId { get; init; }

    /// <summary>Gets the file-level retention decision.</summary>
    public DuplicateGroupMemberDecision Decision { get; init; }

    /// <summary>Gets the fixed total retention score.</summary>
    public int RetentionScore { get; init; }

    /// <summary>Gets the complete category and subreason score document.</summary>
    public required string ScoreJson { get; init; }

    /// <summary>Gets the administrator-facing decision reason.</summary>
    public required string Reason { get; init; }
}
