using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.AssSubsetter.Services;

/// <summary>
/// Shared utility methods for locating original ASS subtitle files associated with a video.
/// </summary>
internal static class AssPathHelper
{
    /// <summary>
    /// Gets the first original (non-subsetted) ASS subtitle file path for a given video path.
    /// </summary>
    /// <param name="videoPath">The physical path of the video file.</param>
    /// <returns>The path to the first matching ASS file, or empty string if none found.</returns>
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Paths are determined solely from trusted database objects.")]
    internal static string GetOriginalAssPath(string? videoPath)
    {
        if (string.IsNullOrEmpty(videoPath))
        {
            return string.Empty;
        }

        string videoDir = Path.GetDirectoryName(videoPath) ?? string.Empty;
        string videoNameWithoutExt = Path.GetFileNameWithoutExtension(videoPath);
        string exactMatch = Path.Join(videoDir, videoNameWithoutExt + ".ass");
        if (File.Exists(exactMatch))
        {
            return exactMatch;
        }

        try
        {
            if (Directory.Exists(videoDir))
            {
                var assFiles = Directory.GetFiles(videoDir, videoNameWithoutExt + "*.ass")
                    .Where(f => !f.Contains("subsetted", StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                if (assFiles.Length > 0)
                {
                    return assFiles[0];
                }
            }
        }
        catch
        {
            /* Ignore directory read exceptions */
        }

        return string.Empty;
    }

    /// <summary>
    /// Gets all original (non-subsetted) ASS subtitle file paths for a given video path.
    /// </summary>
    /// <param name="videoPath">The physical path of the video file.</param>
    /// <returns>An array of matching ASS file paths.</returns>
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Paths are determined solely from trusted database objects.")]
    internal static string[] GetAllOriginalAssPaths(string? videoPath)
    {
        if (string.IsNullOrEmpty(videoPath))
        {
            return [];
        }

        string videoDir = Path.GetDirectoryName(videoPath) ?? string.Empty;
        string videoNameWithoutExt = Path.GetFileNameWithoutExtension(videoPath);

        try
        {
            if (Directory.Exists(videoDir))
            {
                return Directory.GetFiles(videoDir, videoNameWithoutExt + "*.ass")
                    .Where(f => !f.Contains("subsetted", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
            }
        }
        catch
        {
            /* Ignore directory read exceptions */
        }

        return [];
    }
}
