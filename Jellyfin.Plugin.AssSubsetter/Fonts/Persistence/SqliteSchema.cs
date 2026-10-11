using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using static Jellyfin.Plugin.AssSubsetter.Fonts.Persistence.FontDatabaseRows;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <summary>Initializes the catalog from the single EF persistence model.</summary>
public static class SqliteSchema
{
    /// <summary>The supported font catalog schema version.</summary>
    public const int CurrentVersion = 8;

    /// <summary>Creates the current schema and singleton transactionally.</summary>
    /// <param name="connection">An initialized SQLite connection.</param>
    /// <param name="cancellationToken">Cancels schema creation.</param>
    /// <returns>The initialization task.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "PRAGMA statements contain only the compile-time schema version.")]
    public static async Task EnsureCreatedAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        SqliteCommand command = connection.CreateCommand();
        await using var commandScope = command.ConfigureAwait(false);
        command.CommandText = "PRAGMA user_version;";
        int previousVersion = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
        if (previousVersion != 0 && previousVersion != CurrentVersion)
        {
            throw new InvalidDataException($"Font catalog schema version {previousVersion} is not supported; the current version is {CurrentVersion}.");
        }

        if (previousVersion == CurrentVersion)
        {
            return;
        }

        SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);
        FontDbContext context = FontDatabase.CreateContext(connection);
        await using var contextScope = context.ConfigureAwait(false);
        await context.Database.UseTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);
        await context.GetService<IRelationalDatabaseCreator>().CreateTablesAsync(cancellationToken).ConfigureAwait(false);
        context.Add(new SchemaInfoRow { SingletonId = 1 });
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA user_version = {CurrentVersion};";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
