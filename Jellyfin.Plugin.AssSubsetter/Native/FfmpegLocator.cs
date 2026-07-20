using System;
using System.Collections.Generic;
using System.IO;

namespace Jellyfin.Plugin.AssSubsetter.Native;

/// <summary>
///     Locates the FFmpeg executable configured for the Jellyfin server process.
/// </summary>
internal static class FfmpegLocator
{
    private const string FfmpegOption = "--ffmpeg=";
    private const string WellKnownPath = "/usr/lib/jellyfin-ffmpeg/ffmpeg";

    /// <summary>
    ///     Locates Jellyfin's FFmpeg executable using the current process environment.
    /// </summary>
    /// <returns>The executable path, or <see langword="null" /> when it cannot be found.</returns>
    internal static string? FindPath()
    {
        return FindPath(
            Environment.GetEnvironmentVariable("JELLYFIN_FFMPEG_OPT"),
            Environment.GetCommandLineArgs(),
            File.Exists);
    }

    /// <summary>
    ///     Locates Jellyfin's FFmpeg executable using explicit inputs.
    /// </summary>
    /// <param name="ffmpegOptions">The value of JELLYFIN_FFMPEG_OPT.</param>
    /// <param name="commandLineArgs">Server process command-line arguments.</param>
    /// <param name="fileExists">File existence predicate.</param>
    /// <returns>The executable path, or <see langword="null" /> when it cannot be found.</returns>
    internal static string? FindPath(
        string? ffmpegOptions,
        IReadOnlyList<string> commandLineArgs,
        Func<string, bool> fileExists)
    {
        string? candidate = ExtractOption(ffmpegOptions);
        if (candidate is not null && fileExists(candidate))
        {
            return candidate;
        }

        foreach (string argument in commandLineArgs)
        {
            candidate = ExtractCommandLineOption(argument);
            if (candidate is not null && fileExists(candidate))
            {
                return candidate;
            }
        }

        return fileExists(WellKnownPath) ? WellKnownPath : null;
    }

    private static string? ExtractOption(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        int optionIndex = value.IndexOf(FfmpegOption, StringComparison.OrdinalIgnoreCase);
        if (optionIndex < 0)
        {
            return null;
        }

        ReadOnlySpan<char> remainder = value.AsSpan(optionIndex + FfmpegOption.Length).TrimStart();
        if (remainder.IsEmpty)
        {
            return null;
        }

        if (remainder[0] == '"')
        {
            remainder = remainder.Slice(1);
            int closingQuote = remainder.IndexOf('"');
            return closingQuote >= 0 ? remainder.Slice(0, closingQuote).ToString() : null;
        }

        int separator = remainder.IndexOfAny(' ', '\t');
        return (separator >= 0 ? remainder.Slice(0, separator) : remainder).ToString();
    }

    private static string? ExtractCommandLineOption(string argument)
    {
        if (!argument.StartsWith(FfmpegOption, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string path = argument.Substring(FfmpegOption.Length).Trim();
        if (path.Length >= 2 && path[0] == '"' && path[^1] == '"')
        {
            path = path.Substring(1, path.Length - 2);
        }

        return path.Length == 0 ? null : path;
    }
}
