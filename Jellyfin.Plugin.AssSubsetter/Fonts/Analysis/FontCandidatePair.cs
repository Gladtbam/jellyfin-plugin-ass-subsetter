namespace Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;

/// <summary>Identifies one unordered Face pair selected for direct deep comparison.</summary>
/// <param name="LeftFaceId">The lower persisted Face identifier.</param>
/// <param name="RightFaceId">The higher persisted Face identifier.</param>
public sealed record FontCandidatePair(long LeftFaceId, long RightFaceId);
