namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

/// <summary>Read-only path and actual filesystem capabilities.</summary>
/// <param name="IsSafe">Whether all existing components are safe.</param>
/// <param name="IsSameFileSystem">Whether atomic movement is supported between the paths.</param>
/// <param name="Error">The exclusion reason.</param>
public sealed record FontRenameDirectoryProbe(bool IsSafe, bool IsSameFileSystem, string? Error);
