namespace Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;

/// <summary>Maps one persisted Face to its file-level retention evidence.</summary>
/// <param name="FaceId">The persisted Face identifier.</param>
/// <param name="Technology">The rendering technology label.</param>
/// <param name="IsClassifiable">Whether the Face can participate in automatic actions.</param>
/// <param name="Quality">The fixed retention-quality evidence.</param>
public sealed record FontRetentionFace(
    long FaceId,
    string Technology,
    bool IsClassifiable,
    FontRetentionQualityEvidence Quality);
