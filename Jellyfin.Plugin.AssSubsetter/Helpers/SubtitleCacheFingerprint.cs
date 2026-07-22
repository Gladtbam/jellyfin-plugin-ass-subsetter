using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.AssSubsetter.Configuration;

namespace Jellyfin.Plugin.AssSubsetter.Helpers;

/// <summary>
///     Creates compact, versioned cache fingerprints for subtitle artifacts.
/// </summary>
internal static class SubtitleCacheFingerprint
{
    private const int FingerprintByteCount = 12;

    /// <summary>
    ///     Creates a file-name-safe fingerprint from the source metadata and processing inputs.
    /// </summary>
    /// <param name="sourcePath">The source subtitle path.</param>
    /// <param name="mode">The configured subtitle processing mode.</param>
    /// <param name="width">The SUP rendering width.</param>
    /// <param name="height">The SUP rendering height.</param>
    /// <param name="frameRate">The SUP rendering frame rate.</param>
    /// <returns>A 16-character Base64URL fingerprint.</returns>
    internal static string Create(
        string sourcePath,
        SubtitleProcessingMode mode,
        int width = 0,
        int height = 0,
        int frameRate = 0)
    {
        var source = new FileInfo(sourcePath);
        string normalizedPath = Path.GetFullPath(sourcePath);
        if (OperatingSystem.IsWindows())
        {
            normalizedPath = normalizedPath.ToUpperInvariant();
        }

        long sourceLength = source.Exists ? source.Length : -1;
        long lastWriteTicks = source.Exists ? source.LastWriteTimeUtc.Ticks : 0;

        string supParameters = mode == SubtitleProcessingMode.ConvertToSup
            ? string.Create(CultureInfo.InvariantCulture, $"{width}\0{height}\0{frameRate}")
            : string.Empty;
        string canonical = string.Create(
            CultureInfo.InvariantCulture,
            $"v1\0{normalizedPath}\0{sourceLength}\0{lastWriteTicks}\0{(int)mode}\0{supParameters}");

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToBase64String(hash, 0, FingerprintByteCount)
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
