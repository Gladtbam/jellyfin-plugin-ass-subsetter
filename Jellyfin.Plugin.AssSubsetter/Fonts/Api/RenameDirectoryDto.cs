namespace Jellyfin.Plugin.AssSubsetter.Fonts.Api;

/// <summary>Independent directory operation evidence for an administrator.</summary>
/// <param name="Path">The exact directory path.</param>
/// <param name="Action">The recorded create or empty-directory removal action.</param>
/// <param name="Status">The durable directory result, separate from file status.</param>
/// <param name="Error">The retained-directory reason or warning.</param>
public sealed record RenameDirectoryDto(string Path, string Action, string Status, string? Error);
