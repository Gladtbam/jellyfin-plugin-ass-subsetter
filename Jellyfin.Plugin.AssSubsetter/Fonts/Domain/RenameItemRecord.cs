namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>Durable RenameItemRecord font maintenance evidence.</summary>
public sealed record RenameItemRecord
{
    /// <summary>Gets the source execution batch when reading management states.</summary>
    public long? MaintenanceBatchId { get; init; }

    /// <summary>Gets the source authorization when reading management states.</summary>
    public long? OperationRequestId { get; init; }

    /// <summary>Gets the original inventory file identifier.</summary>
    public long FileId { get; init; }

    /// <summary>Gets the original immutable source evidence.</summary>
    public FontFileRecord Evidence { get; init; } = null!;

    /// <summary>Gets the original source path.</summary>
    public string OldPath { get; init; } = string.Empty;

    /// <summary>Gets the planned target path.</summary>
    public string NewPath { get; init; } = string.Empty;

    /// <summary>Gets the immutable root containing both original and target paths.</summary>
    public string SourceRoot { get; init; } = string.Empty;

    /// <summary>Gets the fields and selections actually used by the naming rules.</summary>
    public FontNamingEvidence NamingEvidence { get; init; } = new([], []);

    /// <summary>Gets the fingerprint suffix explicitly applied by the immutable plan.</summary>
    public string? CollisionFingerprint { get; init; }

    /// <summary>Gets the deterministic batch-local case-hop path.</summary>
    public string TemporaryPath { get; init; } = string.Empty;

    /// <summary>Gets the durable transition state.</summary>
    public RenameItemStatus Status { get; init; }

    /// <summary>Gets the administrator-facing skip or failure reason.</summary>
    public string? Error { get; init; }
}
