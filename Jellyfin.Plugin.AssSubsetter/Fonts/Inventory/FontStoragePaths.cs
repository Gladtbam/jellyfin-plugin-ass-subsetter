using System.IO;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

/// <summary>
/// Fixed persistent storage locations used by the font catalog and maintenance operations.
/// </summary>
/// <param name="RootPath">The plugin-specific persistent data directory.</param>
/// <param name="DatabasePath">The SQLite font catalog path.</param>
/// <param name="QuarantinePath">The duplicate-font quarantine directory.</param>
/// <param name="ManifestPath">The durable maintenance manifest directory.</param>
public sealed record FontStoragePaths(
    string RootPath,
    string DatabasePath,
    string QuarantinePath,
    string ManifestPath)
{
    /// <summary>
    /// Creates the fixed storage paths beneath Jellyfin's data directory.
    /// </summary>
    /// <param name="dataPath">Jellyfin's persistent data directory.</param>
    /// <returns>The plugin's fixed font storage paths.</returns>
    public static FontStoragePaths Create(string dataPath)
    {
        string root = Path.Join(dataPath, "ass-subsetter");
        return new FontStoragePaths(
            root,
            Path.Join(root, "ass-subsetter.db"),
            Path.Join(root, "font-quarantine"),
            Path.Join(root, "manifests"));
    }
}
