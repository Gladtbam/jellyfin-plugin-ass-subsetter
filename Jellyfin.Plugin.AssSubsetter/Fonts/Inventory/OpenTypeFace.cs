using System.Collections.Generic;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

/// <summary>
/// One Face directory parsed from an SFNT file or TrueType/OpenType collection.
/// </summary>
/// <param name="FaceIndex">The zero-based Face index in the source container.</param>
/// <param name="DirectoryOffset">The absolute SFNT directory offset.</param>
/// <param name="ScalerType">The SFNT scaler signature.</param>
/// <param name="Tables">The unique, bounds-checked table map.</param>
public sealed record OpenTypeFace(
    int FaceIndex,
    int DirectoryOffset,
    uint ScalerType,
    IReadOnlyDictionary<uint, OpenTypeTable> Tables);
