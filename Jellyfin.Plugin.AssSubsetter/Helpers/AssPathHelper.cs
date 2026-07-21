using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.AssSubsetter.Helpers;

/// <summary>
///     Shared utility methods for locating original ASS subtitle files associated with a video.
/// </summary>
internal static class AssPathHelper
{
    /// <summary>
    ///     Gets all original (non-subsetted) ASS subtitle file paths for a given video path.
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
        catch (IOException)
        {
            /* Ignore directory read exceptions */
        }
        catch (UnauthorizedAccessException)
        {
            /* Ignore directory read exceptions */
        }

        return [];
    }
}
