using System.Collections.Generic;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>Immutable FontRenameCandidateSelection data for current rename candidates.</summary>
/// <param name="Context">The Context value.</param>
/// <param name="Items">The Items value.</param>
public sealed record FontRenameCandidateSelection(FontRenameCandidateReadContext Context, IReadOnlyList<FontRenameCandidate> Items);
