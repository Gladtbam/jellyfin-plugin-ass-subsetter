using System;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

/// <summary>Records one retained physical Font File required by quarantine and expiry.</summary>
/// <param name="FileId">The retained physical-file identifier.</param>
/// <param name="Path">The retained file path stored as immutable evidence.</param>
/// <param name="Size">The retained file length.</param>
/// <param name="Sha256">The retained file SHA-256 digest.</param>
public sealed record QuarantineReplacementEvidence(long FileId, string Path, long Size, ReadOnlyMemory<byte> Sha256);
