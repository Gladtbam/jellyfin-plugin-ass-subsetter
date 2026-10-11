namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>Durable FontRenameCandidate font maintenance evidence.</summary>
public sealed record FontRenameCandidate
{
    /// <summary>Gets the complete immutable source evidence.</summary>
    public FontFileRecord File { get; init; } = null!;

    /// <summary>Gets the owning physical custom root, or null for an excluded source.</summary>
    public string? SourceRoot { get; init; }

    /// <summary>Gets the fields and selections actually used by this item's templates.</summary>
    public FontNamingEvidence NamingEvidence { get; init; } = new([], []);

    /// <summary>Gets the proposed complete target path or null when naming failed.</summary>
    public string? NewPath { get; init; }

    /// <summary>Gets the safety exclusion reason or null for eligible proposals.</summary>
    public string? SkipReason { get; init; }

    /// <summary>Gets the fingerprint suffix explicitly applied by collision resolution.</summary>
    public string? CollisionFingerprint { get; init; }
}
