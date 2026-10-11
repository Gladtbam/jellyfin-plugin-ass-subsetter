using System.Collections.Generic;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>A single consistent database snapshot of rename state.</summary>
/// <param name="Context">The compatible publication and candidate metadata.</param>
/// <param name="Items">The bounded page.</param>
/// <param name="TotalCount">The exact count under the same snapshot.</param>
public sealed record FontRenameStatePage(FontRenameCandidateReadContext Context, IReadOnlyList<FontRenameStateRow> Items, long TotalCount);
