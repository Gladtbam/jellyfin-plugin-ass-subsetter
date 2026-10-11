namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

/// <summary>The result of one validated directory transition.</summary>
/// <param name="Completed">Whether the requested state was reached.</param>
/// <param name="CreatedByOperation">Whether this call created the directory; null when uncertain.</param>
/// <param name="Error">The retained or failure reason.</param>
public sealed record FontRenameDirectoryResult(bool Completed, bool? CreatedByOperation, string? Error);
