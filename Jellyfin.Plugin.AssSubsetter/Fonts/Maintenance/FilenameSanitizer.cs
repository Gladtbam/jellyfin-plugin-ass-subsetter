using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

/// <summary>Builds portable Unicode filename components without rewriting font data.</summary>
public static partial class FilenameSanitizer
{
    /// <summary>Removes cross-platform forbidden characters and normalizes separators.</summary>
    /// <param name="value">The original name evidence.</param>
    /// <returns>A portable component preserving Unicode letters.</returns>
    public static string Sanitize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string cleaned = InvalidFilenameCharactersRegex().Replace(value.Normalize(NormalizationForm.FormC), "-");
        cleaned = FilenameSeparatorsRegex().Replace(cleaned, "-").Trim(' ', '.', '-');
        cleaned = FilenameWhitespaceRegex().Replace(cleaned, " ");
        string stem = cleaned.Split('.')[0];
        if (ReservedDeviceNameRegex().IsMatch(stem))
        {
            cleaned = "_" + cleaned;
        }

        return cleaned;
    }

    /// <summary>Bounds a generated filename while preserving its disambiguation suffix and extension.</summary>
    /// <param name="stem">The normalized base identity.</param>
    /// <param name="extension">The original lowercase extension.</param>
    /// <param name="suffix">The protected template tail and optional collision fingerprint.</param>
    /// <param name="maximumLength">The available UTF-8 filename byte budget.</param>
    /// <returns>The bounded filename.</returns>
    public static string LimitPath(string stem, string extension, string suffix, int maximumLength)
    {
        ArgumentNullException.ThrowIfNull(stem);
        ArgumentNullException.ThrowIfNull(extension);
        int available = Math.Min(240, maximumLength) - Encoding.UTF8.GetByteCount(extension + suffix);
        if (available < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLength), "The parent path leaves no room for a safe filename.");
        }

        var bounded = new StringBuilder();
        int byteCount = 0;
        foreach (Rune rune in stem.EnumerateRunes())
        {
            if (byteCount + rune.Utf8SequenceLength > available)
            {
                break;
            }

            bounded.Append(rune.ToString());
            byteCount += rune.Utf8SequenceLength;
        }

        return bounded.ToString().TrimEnd(' ', '.') + suffix + extension.ToLowerInvariant();
    }

    /// <summary>Gets the pattern for filename characters forbidden on supported platforms.</summary>
    /// <returns>The shared validation and sanitization pattern.</returns>
    [GeneratedRegex("[<>:\"/\\\\|?*\\p{Cc}]", RegexOptions.CultureInvariant)]
    internal static partial Regex InvalidFilenameCharactersRegex();

    /// <summary>Gets the pattern for reserved Windows device names.</summary>
    /// <returns>The pattern applied to the part before the first period.</returns>
    [GeneratedRegex("^(CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex ReservedDeviceNameRegex();

    [GeneratedRegex("[-_]+", RegexOptions.CultureInvariant)]
    private static partial Regex FilenameSeparatorsRegex();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex FilenameWhitespaceRegex();
}
