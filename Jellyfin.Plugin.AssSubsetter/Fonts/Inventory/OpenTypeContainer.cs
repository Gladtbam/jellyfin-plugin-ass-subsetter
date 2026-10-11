using System.Collections.Generic;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

/// <summary>
/// A validated single-Face SFNT or multi-Face TrueType/OpenType collection.
/// </summary>
/// <param name="IsCollection">Whether the source uses a TTC/OTC header.</param>
/// <param name="Faces">The parsed Faces in source order.</param>
public sealed record OpenTypeContainer(bool IsCollection, IReadOnlyList<OpenTypeFace> Faces);
