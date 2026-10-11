using System.Collections.Generic;
using Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Api;

/// <summary>Exposes one safe explainable Face score in an analysis report.</summary>
/// <param name="FaceId">The generation-bound Face identifier.</param>
/// <param name="RetentionScore">The persisted total score.</param>
/// <param name="Components">All five fixed score components.</param>
/// <param name="Verification">Allowlisted passed, failed, and unknown verification outcomes.</param>
public sealed record FontReportScoreDto(long FaceId, int RetentionScore, RetentionScoreBreakdownDto Components, IReadOnlyDictionary<string, FontVerificationStatus> Verification);
