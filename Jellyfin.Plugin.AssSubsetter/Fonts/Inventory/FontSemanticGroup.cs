namespace Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

/// <summary>
/// Functional table groups hashed independently for ordered duplicate evidence.
/// </summary>
public enum FontSemanticGroup
{
    /// <summary>Names, signatures, and non-rendering metadata.</summary>
    Metadata,

    /// <summary>Character and variation-selector mapping.</summary>
    Mapping,

    /// <summary>Outlines, glyph metrics, and shared scaling data.</summary>
    OutlinesAndMetrics,

    /// <summary>Substitution, positioning, kerning, and layout data.</summary>
    Layout,

    /// <summary>TrueType Hinting programs and device metrics.</summary>
    Hinting,

    /// <summary>Color, SVG, and bitmap glyph representations.</summary>
    ColorAndBitmap,

    /// <summary>Variable-font axes, deltas, and named style data.</summary>
    Variation,

    /// <summary>Unrecognized tables retained as functional evidence.</summary>
    OtherFunctional
}
