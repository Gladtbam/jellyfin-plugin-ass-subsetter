using System.Collections.Generic;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Api;

/// <summary>Describes a group with structured relationship evidence and safe diagnostic data.</summary>
/// <param name="Group">The compact group data.</param>
/// <param name="Pairs">The pair relationships connected to group Faces.</param>
/// <param name="HardConstraints">Named hard constraints derived from persisted pair evidence.</param>
/// <param name="SkipReasons">Persisted safe member reasons that prevent a candidate action.</param>
public sealed record FontGroupDetailDto(FontGroupDto Group, IReadOnlyList<PairEvidenceDto> Pairs, IReadOnlyList<string> HardConstraints, IReadOnlyList<string> SkipReasons);
