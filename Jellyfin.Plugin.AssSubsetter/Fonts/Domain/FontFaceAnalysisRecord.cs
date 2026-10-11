namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>Persists the analyzed quality and explainable runtime ordering for one generation-specific Face.</summary>
public sealed record FontFaceAnalysisRecord
{
    /// <summary>Gets the inventory Face identifier.</summary>
    public long FaceId { get; init; }

    /// <summary>Gets the physical-file identifier.</summary>
    public long FileId { get; init; }

    /// <summary>Gets the five-category retention total.</summary>
    public int RetentionScore { get; init; }

    /// <summary>Gets a value indicating whether the Face is a best-quality runtime candidate for its matching identity.</summary>
    public bool IsRuntimePreferred { get; init; }

    /// <summary>Gets the complete five-component score and subreason document.</summary>
    public required string ScoreJson { get; init; }

    /// <summary>Gets independently verified, failed and unknown quality outcomes.</summary>
    public required string VerificationJson { get; init; }
}
