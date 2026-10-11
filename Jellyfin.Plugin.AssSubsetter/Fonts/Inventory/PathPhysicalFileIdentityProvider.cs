using System.IO;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

/// <summary>
/// Conservative identity provider that never merges distinct normalized paths without platform proof.
/// </summary>
public sealed class PathPhysicalFileIdentityProvider : IPhysicalFileIdentityProvider
{
    /// <summary>Returns the normalized path as a conservative physical identity.</summary>
    /// <param name="path">An existing physical font path.</param>
    /// <returns>The normalized path identity.</returns>
    public string GetIdentity(string path) => Path.GetFullPath(path);
}
