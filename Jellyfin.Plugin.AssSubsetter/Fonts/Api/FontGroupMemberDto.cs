using System.Text.Json.Serialization;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Api;

/// <summary>Describes a duplicate-group member and fixed retention-score breakdown.</summary>
/// <param name="FileId">The persisted physical-file identifier.</param>
/// <param name="FaceId">The persisted Face identifier.</param>
/// <param name="Decision">The persisted retention decision.</param>
/// <param name="RetentionScore">The fixed total retention score.</param>
/// <param name="Reason">The persisted administrator decision reason.</param>
/// <param name="Score">The fixed weighted sub-score shape.</param>
public sealed record FontGroupMemberDto(long FileId, long FaceId, DuplicateGroupMemberDecision Decision, int RetentionScore, string Reason, RetentionScoreBreakdownDto Score)
{
    /// <summary>Gets the inventory identity for this Face when loading group details.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FontFaceComparisonDto? Face { get; init; }
}
