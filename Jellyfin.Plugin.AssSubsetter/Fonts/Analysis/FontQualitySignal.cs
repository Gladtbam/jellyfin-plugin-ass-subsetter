namespace Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;

/// <summary>Identifies one fixed, versioned retention-quality subreason.</summary>
public enum FontQualitySignal
{
    /// <summary>Shared encoded glyph behavior is verified.</summary>
    CommonGlyphContentCorrect,

    /// <summary>Required character coverage is complete.</summary>
    CoverageComplete,

    /// <summary>Required cmap records are structurally valid.</summary>
    ValidCmap,

    /// <summary>Layout behavior is complete.</summary>
    LayoutComplete,

    /// <summary>Vertical metrics and layout are complete.</summary>
    VerticalMetricsComplete,

    /// <summary>Variation-selector mappings are complete.</summary>
    VariationSelectorsComplete,

    /// <summary>Color and bitmap glyph content is complete.</summary>
    ColorGlyphsComplete,

    /// <summary>Variable-font axes and deltas are complete.</summary>
    VariationAxesComplete,

    /// <summary>Primary version declarations are reliable.</summary>
    ReliableVersion,

    /// <summary>Independent version declarations agree.</summary>
    ConsistentVersionSources,

    /// <summary>Checksums are valid.</summary>
    ValidChecksums,

    /// <summary>Required tables are structurally valid.</summary>
    ValidTables,

    /// <summary>Runtime native loaders accept the Face consistently.</summary>
    StableNativeLoaders,

    /// <summary>The rendering technology is compatible with direct replacements.</summary>
    CompatibleTechnology,

    /// <summary>Embedding rights are clear and no more restrictive than alternatives.</summary>
    EmbeddingRights,

    /// <summary>Vendor declarations are coherent.</summary>
    CoherentVendor,

    /// <summary>A valid PostScript name is present.</summary>
    PostScriptName,

    /// <summary>A complete typographic family and subfamily pair is present.</summary>
    TypographicNames,

    /// <summary>A complete WWS family and subfamily pair is present.</summary>
    WwsNames,

    /// <summary>A complete legacy family and subfamily pair is present.</summary>
    LegacyNames,

    /// <summary>A valid full name is present.</summary>
    FullName,

    /// <summary>A valid unique identifier is present.</summary>
    UniqueId,

    /// <summary>Lookup aliases are complete and internally consistent.</summary>
    AliasesComplete
}
