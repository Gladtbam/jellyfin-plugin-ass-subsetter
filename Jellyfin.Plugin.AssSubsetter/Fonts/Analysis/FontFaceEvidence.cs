using System;
using System.Collections.Generic;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;

/// <summary>Contains the bounded deep evidence needed to classify one candidate Face pair.</summary>
public sealed record FontFaceEvidence
{
    /// <summary>Gets the persisted Face identifier.</summary>
    public long FaceId { get; init; }

    /// <summary>Gets the persisted containing-file identifier.</summary>
    public long FileId { get; init; }

    /// <summary>Gets a value indicating whether parsing produced complete safe evidence.</summary>
    public bool IsClassifiable { get; init; }

    /// <summary>Gets the reason evidence is incomplete.</summary>
    public string? ClassificationError { get; init; }

    /// <summary>Gets a value indicating whether every encoded character has complete rendering evidence.</summary>
    public bool IsDeepEvidenceComplete { get; init; }

    /// <summary>Gets the stable identity of the containing physical file.</summary>
    public required string PhysicalFileId { get; init; }

    /// <summary>Gets the zero-based Face index inside the containing file.</summary>
    public int FaceIndex { get; init; }

    /// <summary>Gets the SHA-256 digest of the complete containing file.</summary>
    public ReadOnlyMemory<byte> FileHash { get; init; }

    /// <summary>Gets the canonical strict Face digest.</summary>
    public ReadOnlyMemory<byte> StrictFingerprint { get; init; }

    /// <summary>Gets normalized digests partitioned by semantic responsibility.</summary>
    public IReadOnlyDictionary<FontSemanticGroup, ReadOnlyMemory<byte>> SemanticFingerprints { get; init; }
        = new Dictionary<FontSemanticGroup, ReadOnlyMemory<byte>>();

    /// <summary>Gets normalized identity and version declarations.</summary>
    public required FontIdentityEvidence Identity { get; init; }

    /// <summary>Gets the outline and rendering technology label.</summary>
    public required string Technology { get; init; }

    /// <summary>Gets the design-space units per em.</summary>
    public int UnitsPerEm { get; init; }

    /// <summary>Gets a value indicating whether the Face declares fixed-pitch glyphs.</summary>
    public bool IsFixedPitch { get; init; }

    /// <summary>Gets normalized encoded Unicode coverage ranges.</summary>
    public IReadOnlyList<UnicodeRange> UnicodeCoverage { get; init; } = [];

    /// <summary>Gets supported Unicode variation selectors.</summary>
    public IReadOnlySet<uint> VariationSelectors { get; init; } = new HashSet<uint>();

    /// <summary>Gets complete rendering evidence keyed by selector shifted 21 bits plus base scalar.</summary>
    public IReadOnlyDictionary<ulong, ReadOnlyMemory<byte>> VariationFingerprints { get; init; }
        = new Dictionary<ulong, ReadOnlyMemory<byte>>();

    /// <summary>Gets normalized rendering fingerprints for every comparable encoded character.</summary>
    public IReadOnlyDictionary<uint, ReadOnlyMemory<byte>> CharacterFingerprints { get; init; }
        = new Dictionary<uint, ReadOnlyMemory<byte>>();
}
