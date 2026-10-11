namespace Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

/// <summary>
/// Supplies a platform-stable identity used to group hard-linked paths.
/// </summary>
public interface IPhysicalFileIdentityProvider
{
    /// <summary>Reads the current native hard-link count when the platform supports reliable evidence.</summary>
    /// <param name="path">The existing file path.</param>
    /// <returns>The native link count, or null when unavailable.</returns>
    uint? GetLinkCount(string path) => null;

    /// <summary>Returns an identity shared by paths to the same physical file.</summary>
    /// <param name="path">An existing physical font path.</param>
    /// <returns>A stable identity within the current scan.</returns>
    string GetIdentity(string path);
}
