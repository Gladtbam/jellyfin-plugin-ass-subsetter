using System.Collections.Generic;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>Immutable FontRenameCandidatePreparation data for current rename candidates.</summary>
/// <param name="State">The State value.</param>
/// <param name="Items">The Items value.</param>
public sealed record FontRenameCandidatePreparation(FontRenameCandidateState State, IReadOnlyList<FontRenameCandidate> Items);
