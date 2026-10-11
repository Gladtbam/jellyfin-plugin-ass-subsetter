namespace Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

/// <summary>Describes one validated default or explicit Unicode variation mapping.</summary>
/// <param name="CodePoint">The base Unicode scalar.</param>
/// <param name="Selector">The Unicode variation selector.</param>
/// <param name="GlyphId">The explicit glyph, or null when the ordinary cmap supplies the glyph.</param>
public sealed record UnicodeVariationMapping(uint CodePoint, uint Selector, ushort? GlyphId);
