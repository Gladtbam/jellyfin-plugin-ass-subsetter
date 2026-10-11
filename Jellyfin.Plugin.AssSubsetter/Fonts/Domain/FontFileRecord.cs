using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>
/// Immutable inventory evidence for one physical font file.
/// </summary>
public sealed record FontFileRecord
{
    /// <summary>Gets the database identifier, or zero before persistence.</summary>
    public long Id { get; init; }

    /// <summary>Gets the inventory generation identifier.</summary>
    public long GenerationId { get; init; }

    /// <summary>Gets the observed path.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the normalized path key.</summary>
    public required string NormalizedPath { get; init; }

    /// <summary>Gets the platform physical-file identity.</summary>
    public required string PhysicalIdentity { get; init; }

    /// <summary>Gets every additional observed hard-link path to the same physical file.</summary>
    public IReadOnlyList<string> PathAliases { get; init; } = [];

    /// <summary>Gets the file size in bytes.</summary>
    public long Size { get; init; }

    /// <summary>Gets the last-write time in UTC.</summary>
    public DateTimeOffset ModifiedUtc { get; init; }

    /// <summary>Gets the physical file SHA-256 digest.</summary>
    public required ReadOnlyMemory<byte> Sha256 { get; init; }

    /// <summary>Gets the directory policy flags.</summary>
    public FontDirectoryPolicy DirectoryPolicy { get; init; }

    /// <summary>Gets the observed link kind.</summary>
    public FontLinkKind LinkKind { get; init; }

    /// <summary>Gets the inspection health.</summary>
    public FontFileHealth Health { get; init; }

    /// <summary>Gets an inspection error suitable for an administrator report.</summary>
    public string? Error { get; init; }

    /// <summary>Gets the Faces stored in this physical file.</summary>
    public required IReadOnlyList<FontFaceRecord> Faces { get; init; }
}
