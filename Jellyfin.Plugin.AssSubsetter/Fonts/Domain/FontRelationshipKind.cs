using System.Diagnostics.CodeAnalysis;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>
/// Ordered, first-match relationship between two font Faces.
/// </summary>
[SuppressMessage("Design", "CA1008:Enums should have zero value", Justification = "Values 1 through 14 are the approved ordered first-match chain; Unclassifiable is intentionally outside it.")]
public enum FontRelationshipKind
{
    /// <summary>Corresponding Faces come from physically identical files.</summary>
    IdenticalFileHash = 1,

    /// <summary>The canonical Face content is identical.</summary>
    StrictFaceDuplicate = 2,

    /// <summary>Rendering and layout behavior are equivalent.</summary>
    RenderingAndLayoutEquivalent = 3,

    /// <summary>Only metadata differs.</summary>
    MetadataOnlyDifference = 4,

    /// <summary>One Face is a proven coverage superset of the other.</summary>
    CoverageSuperset = 5,

    /// <summary>Coverage differs without a safe superset direction.</summary>
    CoverageVariant = 6,

    /// <summary>Only a bounded number of glyphs differ.</summary>
    SmallGlyphDifference = 7,

    /// <summary>Only layout tables differ.</summary>
    LayoutOnlyDifference = 8,

    /// <summary>Only Hinting differs.</summary>
    HintingOnlyDifference = 9,

    /// <summary>Reliable version evidence orders conflicting content.</summary>
    VersionConflict = 10,

    /// <summary>The same identity has conflicting functional content.</summary>
    SameIdentityContentConflict = 11,

    /// <summary>The PostScript name conflicts.</summary>
    PostScriptNameConflict = 12,

    /// <summary>Names intersect in a conflicting way.</summary>
    CrossNameConflict = 13,

    /// <summary>The Faces are independent.</summary>
    Independent = 14,

    /// <summary>Available evidence is insufficient for classification.</summary>
    Unclassifiable = 100
}
