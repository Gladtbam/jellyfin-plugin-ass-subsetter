using System;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>
/// Administrator-facing SQLite health snapshot.
/// </summary>
public sealed record FontDatabaseHealth
{
    /// <summary>Gets the active database path.</summary>
    public required string DatabasePath { get; init; }

    /// <summary>Gets the current schema version.</summary>
    public int SchemaVersion { get; init; }

    /// <summary>Gets the SQLite journal mode.</summary>
    public required string JournalMode { get; init; }

    /// <summary>Gets the database file size.</summary>
    public long Size { get; init; }

    /// <summary>Gets the currently published inventory generation, if any.</summary>
    public long? PublishedGenerationId { get; init; }

    /// <summary>Gets the time at which live database health was checked.</summary>
    public DateTimeOffset CheckedUtc { get; init; }

    /// <summary>Gets the last preserved corrupt database path, if any.</summary>
    public string? RecoveredCorruptDatabasePath { get; init; }
}
