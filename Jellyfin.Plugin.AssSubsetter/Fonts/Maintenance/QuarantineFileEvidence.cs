using System;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

/// <summary>Describes current filesystem evidence used before a destructive transition.</summary>
/// <param name="Size">The current file length.</param>
/// <param name="ModifiedUtc">The current last-write time.</param>
/// <param name="Sha256">The current SHA-256 digest.</param>
/// <param name="PhysicalIdentity">The current native file identity, or null when unverified.</param>
/// <param name="LinkCount">The current native hard-link count, or null when unverified.</param>
public sealed record QuarantineFileEvidence(long Size, DateTimeOffset ModifiedUtc, ReadOnlyMemory<byte> Sha256, string? PhysicalIdentity = null, uint? LinkCount = null);
