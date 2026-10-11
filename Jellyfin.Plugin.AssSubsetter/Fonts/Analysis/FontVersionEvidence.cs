using System.Collections.Generic;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;

/// <summary>Describes whether declared font versions can safely order two Faces.</summary>
/// <param name="Value">The agreed numeric version, or <see langword="null"/> when evidence conflicts or is absent.</param>
/// <param name="IsReliable">Whether the available declarations agree and include a primary version source.</param>
/// <param name="Sources">The evidence fields that participated in the decision.</param>
public sealed record FontVersionEvidence(decimal? Value, bool IsReliable, IReadOnlyList<string> Sources);
