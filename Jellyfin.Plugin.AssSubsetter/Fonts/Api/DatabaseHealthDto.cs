using System;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Api;

/// <summary>Describes administrator-safe SQLite health data.</summary>
/// <param name="SchemaVersion">The current schema version.</param>
/// <param name="JournalMode">The current SQLite journal mode.</param>
/// <param name="Size">The database size in bytes.</param>
/// <param name="PublishedGenerationId">The published inventory generation, if any.</param>
/// <param name="CheckedUtc">The time at which the live health query completed.</param>
/// <param name="RecoveredCorruptDatabasePath">The preserved corrupt-database path, if any.</param>
public sealed record DatabaseHealthDto(int SchemaVersion, string JournalMode, long Size, long? PublishedGenerationId, DateTimeOffset CheckedUtc, string? RecoveredCorruptDatabasePath);
