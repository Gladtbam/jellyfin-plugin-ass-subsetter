using System.Collections.Generic;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;

/// <summary>Contains the fixed five-category retention score and every persisted subreason.</summary>
/// <param name="Content">Content completeness points, capped at 40.</param>
/// <param name="Version">Version reliability and recency points, capped at 25.</param>
/// <param name="Technical">Technical integrity points, capped at 20.</param>
/// <param name="Names">Name completeness points, capped at 10.</param>
/// <param name="Modified">Modification-time points, capped at 5.</param>
/// <param name="Subreasons">The stable subreason-to-points breakdown.</param>
public sealed record RetentionScore(
    int Content,
    int Version,
    int Technical,
    int Names,
    int Modified,
    IReadOnlyDictionary<string, int> Subreasons)
{
    /// <summary>Gets the sum of all five fixed categories.</summary>
    public int Total => Content + Version + Technical + Names + Modified;
}
