using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>Immutable, versioned administrator naming rules.</summary>
/// <param name="FileNameTemplate">The filename stem template.</param>
/// <param name="FolderTemplate">The root-relative directory template.</param>
/// <param name="RemoveEmptyDirectories">Whether to remove empty source ancestors.</param>
/// <param name="FormatVersion">The text template format version.</param>
public sealed record FontNamingRules(string FileNameTemplate, string FolderTemplate, bool RemoveEmptyDirectories, int FormatVersion = FontNamingRules.CurrentFormatVersion)
{
    /// <summary>The supported text template format.</summary>
    public const int CurrentFormatVersion = 2;

    /// <summary>The ordinary placeholder template used for new configurations and reset.</summary>
    public const string DefaultFileNameTemplate = "{family}{styleSuffix}{collectionSuffix}{versionSuffix}";

    /// <summary>Computes a deterministic digest of the exact rules.</summary>
    /// <returns>The lowercase SHA-256 digest.</returns>
    public string GetFingerprint() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this)))).ToLowerInvariant();
}
