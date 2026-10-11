using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using DomainPolicy = Jellyfin.Plugin.AssSubsetter.Fonts.Domain.FontDirectoryPolicy;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

/// <summary>
/// Resolves scan roots and immutable safety flags for system and configured font directories.
/// </summary>
public sealed class FontDirectoryPolicyResolver
{
    private static readonly char[] Separators = [';', ','];
    private readonly IReadOnlyList<FontDirectoryRoot> _roots;

    /// <summary>Initializes a new instance of the <see cref="FontDirectoryPolicyResolver"/> class.</summary>
    /// <param name="customDirectories">Configured scan directories.</param>
    /// <param name="protectedDirectories">Additional directories that maintenance must never modify.</param>
    /// <param name="preferredDirectories">Directories preferred during runtime and retention selection.</param>
    /// <param name="includePlatformDefaults">Whether protected platform font roots should be included.</param>
    public FontDirectoryPolicyResolver(
        string? customDirectories,
        string? protectedDirectories,
        string? preferredDirectories,
        bool includePlatformDefaults = true)
    {
        var policies = new Dictionary<string, DomainPolicy>(FontPathComparison.Comparer);
        if (includePlatformDefaults)
        {
            foreach (string path in GetPlatformDefaults())
            {
                AddPolicy(policies, path, DomainPolicy.Protected);
            }
        }

        foreach (string path in Split(customDirectories))
        {
            AddPolicy(policies, path, DomainPolicy.Custom);
        }

        foreach (string path in Split(protectedDirectories))
        {
            AddPolicy(policies, path, DomainPolicy.Protected);
        }

        foreach (string path in Split(preferredDirectories))
        {
            AddPolicy(policies, path, DomainPolicy.Preferred);
        }

        _roots = policies.Select(pair => new FontDirectoryRoot(pair.Key, pair.Value)).OrderBy(root => root.Path, FontPathComparison.Comparer).ToArray();
    }

    /// <summary>Gets all normalized configured roots, including temporarily unavailable roots.</summary>
    /// <returns>Canonical policy inputs.</returns>
    public IReadOnlyList<FontDirectoryRoot> GetConfiguredRoots() => _roots;

    /// <summary>Gets normalized existing roots eligible for inventory scanning.</summary>
    /// <returns>The roots with their directly configured policy flags.</returns>
    public IReadOnlyList<FontDirectoryRoot> GetScanRoots() => _roots.Where(root => Directory.Exists(root.Path)).ToArray();

    /// <summary>Combines every ancestor policy that applies to a font path.</summary>
    /// <param name="path">The observed font path.</param>
    /// <returns>The combined protected, preferred, and custom flags.</returns>
    public DomainPolicy GetPolicy(string path)
    {
        string fullPath = Path.GetFullPath(path);
        DomainPolicy policy = DomainPolicy.None;
        foreach (FontDirectoryRoot root in _roots)
        {
            if (FontPathComparison.IsWithin(fullPath, root.Path))
            {
                policy |= root.Policy;
            }
        }

        return policy;
    }

    /// <summary>Finds the most specific existing custom physical root, excluding protected paths.</summary>
    /// <param name="path">The existing or generated path.</param>
    /// <returns>The owning root, or null when maintenance is not authorized.</returns>
    public string? GetOwningCustomRoot(string path)
    {
        if ((GetPolicy(path) & DomainPolicy.Protected) != 0)
        {
            return null;
        }

        return _roots.Where(root => (root.Policy & DomainPolicy.Custom) != 0 && Directory.Exists(root.Path) && FontPathComparison.IsWithin(path, root.Path))
            .OrderByDescending(root => root.Path.Length)
            .Select(root => root.Path)
            .FirstOrDefault();
    }

    /// <summary>Reports whether a file extension is part of the supported SFNT inventory.</summary>
    /// <param name="path">The candidate file path.</param>
    /// <returns><see langword="true"/> for ttf, otf, ttc, or otc files.</returns>
    public static bool IsSupportedFont(string path) => Path.GetExtension(path).ToLowerInvariant() is ".ttf" or ".otf" or ".ttc" or ".otc";

    private static void AddPolicy(Dictionary<string, DomainPolicy> policies, string path, DomainPolicy policy)
    {
        string fullPath = Path.GetFullPath(path.Trim());
        if (Directory.Exists(fullPath))
        {
            FileSystemInfo? target = new DirectoryInfo(fullPath).ResolveLinkTarget(returnFinalTarget: true);
            if (target is not null)
            {
                fullPath = Path.GetFullPath(target.FullName);
            }
        }

        policies[fullPath] = policies.TryGetValue(fullPath, out DomainPolicy existing) ? existing | policy : policy;
    }

    private static string[] Split(string? paths) => string.IsNullOrWhiteSpace(paths)
        ? []
        : paths.Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IEnumerable<string> GetPlatformDefaults()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
            yield return Path.Join(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft",
                "Windows",
                "Fonts");
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/System/Library/Fonts";
            yield return "/Library/Fonts";
            yield return Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Fonts");
        }
        else
        {
            yield return "/usr/share/fonts";
            yield return "/usr/local/share/fonts";
            yield return Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share", "fonts");
        }
    }
}
