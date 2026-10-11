using System.Collections.Generic;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>The fields and name selections used for a filename or folder.</summary>
/// <param name="Fields">Referenced field and dependency evidence.</param>
/// <param name="Selections">Name-pair and collection decisions.</param>
public sealed record FontNamingEvidence(IReadOnlyList<FontNamingFieldEvidence> Fields, IReadOnlyList<FontNamingSelectionEvidence> Selections);
