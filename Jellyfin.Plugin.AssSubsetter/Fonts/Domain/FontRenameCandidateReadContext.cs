namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>Immutable FontRenameCandidateReadContext data for current rename candidates.</summary>
/// <param name="Publication">The Publication value.</param>
/// <param name="State">The State value.</param>
/// <param name="IsCurrent">The IsCurrent value.</param>
/// <param name="BlockReason">The BlockReason value.</param>
public sealed record FontRenameCandidateReadContext(FontPublishedAnalysisContext? Publication, FontRenameCandidateState? State, bool IsCurrent, string? BlockReason);
