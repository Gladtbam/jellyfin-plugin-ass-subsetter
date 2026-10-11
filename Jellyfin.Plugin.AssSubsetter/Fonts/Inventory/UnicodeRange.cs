namespace Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

/// <summary>
/// An inclusive, normalized Unicode scalar-value range covered by a font Face.
/// </summary>
/// <param name="Start">The first covered scalar value.</param>
/// <param name="End">The last covered scalar value.</param>
public readonly record struct UnicodeRange(uint Start, uint End);
