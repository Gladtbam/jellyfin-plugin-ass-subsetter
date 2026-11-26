using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <content>Catalog space maintenance, performed outside a SQLite transaction.</content>
public sealed partial class SqliteFontRepository
{
    // VACUUM rewrites the whole catalog. Leave small amounts of free space for the next generation to reuse.
    private const long MinimumReclaimableBytes = 4L * 1024 * 1024;
    private const int MinimumFreePagePercentage = 20;

    /// <summary>Refreshes SQLite query statistics, attempts to truncate the WAL, and compacts the catalog when enough space can be reclaimed.</summary>
    /// <param name="cancellationToken">Cancels the maintenance operation between SQLite commands.</param>
    /// <returns>The bytes reclaimed from the main database and a deferral reason if the database is busy or disk space is insufficient.</returns>
    /// <remarks>Compaction requires at least 4 MiB of free pages and a free page share of at least 20 percent.</remarks>
    public Task<(long ReclaimedBytes, string? DeferredReason)> OptimizeDatabaseAsync(CancellationToken cancellationToken)
        => _database.WriteAsync<(long ReclaimedBytes, string? DeferredReason)>(OptimizeDatabaseCoreAsync, cancellationToken);

    private static async Task<(long ReclaimedBytes, string? DeferredReason)> OptimizeDatabaseCoreAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        SqliteCommand command = connection.CreateCommand();
        await using var commandScope = command.ConfigureAwait(false);
        command.CommandTimeout = 1;
        try
        {
            // Checkpoint should yield to readers pinning the WAL instead of waiting for them.
            command.CommandText = "PRAGMA busy_timeout = 0;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            // Inspect all tables and bound the amount of analysis performed on any index.
            command.CommandText = "PRAGMA main.optimize = 0x10012;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (!await TryTruncateWalAsync(command, cancellationToken).ConfigureAwait(false))
            {
                return (0, "database-busy");
            }

            var (pageCount, freePages, pageSize) = await ReadDatabasePagesAsync(command, cancellationToken).ConfigureAwait(false);
            if (freePages * pageSize < MinimumReclaimableBytes || freePages * 100 < pageCount * MinimumFreePagePercentage)
            {
                return (0, null);
            }

            command.CommandText = "VACUUM;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            // The rebuild can leave the shorter database image in the WAL until it is checkpointed.
            if (!await TryTruncateWalAsync(command, cancellationToken).ConfigureAwait(false))
            {
                return (0, "database-busy");
            }

            var (remainingPages, _, _) = await ReadDatabasePagesAsync(command, cancellationToken).ConfigureAwait(false);
            return (Math.Max(0, pageCount - remainingPages) * pageSize, null);
        }
        catch (SqliteException exception) when (exception.SqliteErrorCode is 5 or 6 or 13)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return (0, exception.SqliteErrorCode == 13 ? "insufficient-space" : "database-busy");
        }
        finally
        {
            // Future connections load the refreshed statistics. Active readers keep their existing snapshots.
            SqliteConnection.ClearPool(connection);
        }
    }

    private static async Task<bool> TryTruncateWalAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        command.CommandText = "PRAGMA main.wal_checkpoint(TRUNCATE);";
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is 0L;
    }

    private static async Task<(long PageCount, long FreePages, long PageSize)> ReadDatabasePagesAsync(
        SqliteCommand command, CancellationToken cancellationToken)
    {
        command.CommandText = "SELECT page_count, freelist_count, page_size FROM pragma_page_count, pragma_freelist_count, pragma_page_size;";
        SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var readerScope = reader.ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("SQLite did not return catalog page counts.");
        }

        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }
}
