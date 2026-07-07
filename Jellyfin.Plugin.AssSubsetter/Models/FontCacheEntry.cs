using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AssSubsetter.Models;

/// <summary>
///     Represents a cached font file's metadata.
/// </summary>
public class FontCacheEntry
{
    /// <summary>
    ///     Gets or sets the path to the physical font file.
    /// </summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the index of the face within a TTC, or 0 for TTF/OTF.
    /// </summary>
    public int FaceIndex { get; set; }

    /// <summary>
    ///     Gets or sets the font type (e.g., TrueType, OpenType).
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the family name of the font.
    /// </summary>
    public string FamilyName { get; set; } = string.Empty;

    /// <summary>
    ///     Gets or sets the style name of the font (e.g., Regular, Bold).
    /// </summary>
    public string Style { get; set; } = string.Empty;

    /// <summary>
    ///     Gets the list of alternative or localized names for the font.
    /// </summary>
    public IReadOnlyList<string> Names { get; init; } = Array.Empty<string>();

    /// <summary>
    ///     Gets or sets the font weight (e.g., 400 for Normal, 700 for Bold).
    /// </summary>
    public int Weight { get; set; }

    /// <summary>
    ///     Gets or sets the font width (e.g., 5 for Normal).
    /// </summary>
    public int Width { get; set; }

    /// <summary>
    ///     Gets or sets a value indicating whether the font is italic.
    /// </summary>
    public bool IsItalic { get; set; }

    /// <summary>
    ///     Gets or sets a value indicating whether the font has a fixed pitch (monospace).
    /// </summary>
    public bool IsFixedPitch { get; set; }

    /// <summary>
    ///     Gets or sets the last write time of the physical file in UTC.
    /// </summary>
    public DateTime LastWriteTimeUtc { get; set; }
}
