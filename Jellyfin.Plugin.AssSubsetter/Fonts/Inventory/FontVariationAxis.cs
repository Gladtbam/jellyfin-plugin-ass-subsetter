namespace Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

/// <summary>
/// One variable-font axis extracted from the OpenType <c>fvar</c> table.
/// </summary>
/// <param name="Tag">The four-character axis tag.</param>
/// <param name="MinimumValue">The minimum axis coordinate.</param>
/// <param name="DefaultValue">The default axis coordinate.</param>
/// <param name="MaximumValue">The maximum axis coordinate.</param>
/// <param name="Flags">The OpenType axis flags.</param>
/// <param name="NameId">The name-table identifier for the axis label.</param>
public sealed record FontVariationAxis(
    uint Tag,
    float MinimumValue,
    float DefaultValue,
    float MaximumValue,
    ushort Flags,
    ushort NameId);
