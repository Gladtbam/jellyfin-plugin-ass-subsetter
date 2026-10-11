using System;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>Applies the host's consistent physical-path comparison policy across font modules.</summary>
internal static class FontPathComparison
{
    public static StringComparer Comparer { get; } = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static StringComparison Comparison { get; } = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Checks containment with a directory separator boundary.</summary>
    /// <param name="path">The candidate path.</param>
    /// <param name="directory">The containing directory.</param>
    /// <returns>Whether the path is the directory or one of its descendants.</returns>
    public static bool IsWithin(string path, string directory)
    {
        string fullPath = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        string root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(directory));
        return fullPath.Equals(root, Comparison)
            || fullPath.StartsWith(root.EndsWith(System.IO.Path.DirectorySeparatorChar) ? root : root + System.IO.Path.DirectorySeparatorChar, Comparison);
    }
}
