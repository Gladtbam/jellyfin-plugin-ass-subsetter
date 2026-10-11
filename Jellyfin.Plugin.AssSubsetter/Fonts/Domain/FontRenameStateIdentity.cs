using System;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>Shared source identity and display categories for candidate and execution rows.</summary>
public static class FontRenameStateIdentity
{
    /// <summary>Identifies one immutable source evidence and exact target.</summary>
    /// <param name="oldPath">The original path.</param>
    /// <param name="newPath">The target path.</param>
    /// <param name="evidence">The immutable source evidence.</param>
    /// <returns>The source-specific digest.</returns>
    public static string SourceKey(string oldPath, string newPath, FontFileRecord evidence)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", oldPath, newPath, evidence.PhysicalIdentity, Convert.ToHexString(evidence.Sha256.Span), evidence.Size, evidence.ModifiedUtc.ToUniversalTime().Ticks))));

    internal static string PathKey(string path) => OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;

    /// <summary>Maps a durable item state to its list filter.</summary>
    /// <param name="status">The execution state.</param>
    /// <param name="error">The current attempt error.</param>
    /// <returns>The stable list category.</returns>
    public static string Category(RenameItemStatus status, string? error) => status switch
    {
        RenameItemStatus.Renamed => "renamed",
        RenameItemStatus.RolledBack => "rolledback",
        RenameItemStatus.FailedIo => "failed",
        RenameItemStatus.Pending or RenameItemStatus.Moving or RenameItemStatus.Temporary or RenameItemStatus.RollingBack => error is null ? "pending" : "failed",
        _ => "skipped"
    };
}
