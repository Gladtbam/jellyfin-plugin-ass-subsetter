using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <summary>
/// Owns SQLite connection policy and serialized writer access.
/// </summary>
public sealed class FontDatabase : IAsyncDisposable
{
    private readonly FontStoragePaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _writerGate = new(1, 1);
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="FontDatabase"/> class.</summary>
    /// <param name="paths">The fixed catalog, quarantine, and manifest paths.</param>
    /// <param name="timeProvider">The clock used to name preserved corrupt database files.</param>
    public FontDatabase(FontStoragePaths paths, TimeProvider? timeProvider = null)
    {
        _paths = paths;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Gets the preserved corrupt database path when initialization recovered from corruption.
    /// </summary>
    public string? RecoveredCorruptDatabasePath { get; private set; }

    internal string DatabasePath => _paths.DatabasePath;

    /// <summary>Initializes the database and its schema.</summary>
    /// <param name="cancellationToken">A token that cancels initialization.</param>
    /// <returns>A task representing initialization.</returns>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_paths.RootPath);
        Directory.CreateDirectory(_paths.QuarantinePath);
        Directory.CreateDirectory(_paths.ManifestPath);

        await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                await InitializeCoreAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode is 11 or 26)
            {
                PreserveCorruptDatabase();
                await InitializeCoreAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _writerGate.Release();
        }
    }

    internal async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Shared cache exposes table-level SQLITE_LOCKED to readers during staged inventory writes.
            // WAL with private caches lets the report keep reading the published snapshot.
            Cache = SqliteCacheMode.Private,
            Pooling = true
        };
        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await ExecutePragmaAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken).ConfigureAwait(false);
        await ExecutePragmaAsync(connection, "PRAGMA busy_timeout = 5000;", cancellationToken).ConfigureAwait(false);
        return connection;
    }

    internal async Task<FontDbContext> OpenContextAsync(CancellationToken cancellationToken)
        => new(await OpenConnectionAsync(cancellationToken).ConfigureAwait(false), ownsConnection: true);

    internal static FontDbContext CreateContext(SqliteConnection connection) => new(connection);

    internal async Task<T> WriteAsync<T>(Func<SqliteConnection, CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var connectionScope = connection.ConfigureAwait(false);
            return await action(connection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writerGate.Release();
        }
    }

    /// <summary>
    /// Releases the serialized writer gate and clears SQLite connection pools so catalog files can be moved or removed safely.
    /// </summary>
    /// <returns>A completed value task.</returns>
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        SqliteConnection.ClearAllPools();
        _writerGate.Dispose();
        return ValueTask.CompletedTask;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Callers pass only hard-coded SQLite PRAGMA statements.")]
    private static async Task ExecutePragmaAsync(SqliteConnection connection, string text, CancellationToken cancellationToken)
    {
        SqliteCommand command = connection.CreateCommand();
        await using var commandScope = command.ConfigureAwait(false);
        command.CommandText = text;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var connectionScope = connection.ConfigureAwait(false);
        await ExecutePragmaAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken).ConfigureAwait(false);
        await ExecutePragmaAsync(connection, "PRAGMA synchronous = NORMAL;", cancellationToken).ConfigureAwait(false);
        await SqliteSchema.EnsureCreatedAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    private void PreserveCorruptDatabase()
    {
        SqliteConnection.ClearAllPools();
        string suffix = ".corrupt-" + _timeProvider.GetUtcNow().ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        foreach (string sourcePath in new[] { _paths.DatabasePath, _paths.DatabasePath + "-wal", _paths.DatabasePath + "-shm" })
        {
            if (!File.Exists(sourcePath))
            {
                continue;
            }

            string preservedPath = sourcePath + suffix;
            File.Move(sourcePath, preservedPath);
            if (string.Equals(sourcePath, _paths.DatabasePath, StringComparison.Ordinal))
            {
                RecoveredCorruptDatabasePath = preservedPath;
            }
        }
    }
}
