using System.Collections.Generic;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance.Naming;

/// <summary>An immutable text template reusable for all files in a plan.</summary>
public sealed class FontNamingProgram
{
    internal FontNamingProgram(FontTemplateKind kind, IReadOnlyList<IReadOnlyList<FontNamingPart>> components)
    {
        Kind = kind;
        Components = components;
    }

    internal FontTemplateKind Kind { get; }

    internal IReadOnlyList<IReadOnlyList<FontNamingPart>> Components { get; }
}
