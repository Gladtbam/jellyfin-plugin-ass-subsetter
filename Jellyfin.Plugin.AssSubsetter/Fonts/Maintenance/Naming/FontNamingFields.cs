using System.Collections.Generic;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance.Naming;

internal sealed class FontNamingFields(IReadOnlyDictionary<string, FontNamingFieldValue> values, IReadOnlyList<FontNamingSelectionEvidence> selections)
{
    internal IReadOnlyList<FontNamingSelectionEvidence> Selections { get; } = selections;

    internal FontNamingFieldValue Get(string name) => values[name];
}
