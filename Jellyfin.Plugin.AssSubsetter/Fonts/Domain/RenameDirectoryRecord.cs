namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>Durable intent and independent result for one directory.</summary>
/// <param name="Path">The exact directory path.</param>
/// <param name="SourceRoot">The immutable source root boundary.</param>
/// <param name="Action">The authorized directory action.</param>
/// <param name="Status">The latest durable result.</param>
/// <param name="CreatedByBatch">Confirmed ownership; null means unknown.</param>
/// <param name="Error">The readable exclusion or warning.</param>
public sealed record RenameDirectoryRecord(string Path, string SourceRoot, RenameDirectoryAction Action, RenameDirectoryStatus Status, bool? CreatedByBatch, string? Error);
