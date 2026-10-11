namespace Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;

/// <summary>Reports one retained or quarantine-candidate physical-file decision.</summary>
/// <param name="FileId">The persisted physical-file identifier.</param>
/// <param name="Name">The file name shown to administrators.</param>
/// <param name="Path">The observed full path.</param>
/// <param name="Score">The fixed score breakdown.</param>
/// <param name="Reason">The hard constraint, quality decision, or tie-break reason.</param>
/// <param name="IsRuntimePreferred">Whether runtime ordering should prefer this retained file inside its relation component.</param>
public sealed record RetentionFileDecision(
    long FileId,
    string Name,
    string Path,
    RetentionScore Score,
    string Reason,
    bool IsRuntimePreferred);
