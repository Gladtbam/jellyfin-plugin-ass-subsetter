using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;

/// <summary>Contains physical-file safety, quality, and tie-break evidence.</summary>
public sealed record FontRetentionFile
{
    /// <summary>Gets the persisted physical-file identifier.</summary>
    public long FileId { get; init; }

    /// <summary>Gets the observed file path.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the normalized deterministic path key.</summary>
    public required string NormalizedPath { get; init; }

    /// <summary>Gets the file name used in administrator reports.</summary>
    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>Gets the physical byte count.</summary>
    public long Size { get; init; }

    /// <summary>Gets the last-write time used only by the final weighted category.</summary>
    public DateTimeOffset ModifiedUtc { get; init; }

    /// <summary>Gets protected, preferred, and custom directory flags.</summary>
    public FontDirectoryPolicy DirectoryPolicy { get; init; }

    /// <summary>Gets the observed link kind.</summary>
    public FontLinkKind LinkKind { get; init; }

    /// <summary>Gets the inspection health.</summary>
    public FontFileHealth Health { get; init; }

    /// <summary>Gets a value indicating whether maintenance lacks the required in-use lease.</summary>
    public bool IsInUseWithoutLease { get; init; }

    /// <summary>Gets every Face contained by this physical file.</summary>
    public required IReadOnlyList<FontRetentionFace> Faces { get; init; }
}
