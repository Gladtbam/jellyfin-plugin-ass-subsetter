using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

/// <summary>
/// Canonical strict and grouped semantic SHA-256 evidence for one Face.
/// </summary>
/// <param name="StrictFingerprint">The digest of every table after approved canonicalization.</param>
/// <param name="SemanticFingerprints">The digest of each present semantic table group.</param>
public sealed record FontFaceFingerprints(
    ReadOnlyMemory<byte> StrictFingerprint,
    IReadOnlyDictionary<FontSemanticGroup, ReadOnlyMemory<byte>> SemanticFingerprints)
{
    /// <summary>Gets a value indicating whether supported field and mapping parsing supplied complete semantic proof.</summary>
    public bool IsSemanticEvidenceComplete { get; init; }
}
