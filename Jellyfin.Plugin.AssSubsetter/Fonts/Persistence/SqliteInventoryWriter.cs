using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <summary>Inventory-only bulk writer; prepares each statement once per transaction and reuses its parameters.</summary>
internal sealed class SqliteInventoryWriter : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private readonly Dictionary<string, SqliteCommand> _commands = new(StringComparer.Ordinal);

    internal SqliteInventoryWriter(SqliteConnection connection, SqliteTransaction transaction)
    {
        _connection = connection;
        _transaction = transaction;
    }

    internal async Task WriteAsync(long generationId, IReadOnlyList<FontFileRecord> files, CancellationToken cancellationToken)
    {
        foreach (FontFileRecord file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SqliteCommand fileCommand = Command(
                """
                INSERT INTO font_file(generation_id, path, normalized_path, physical_identity, size, modified_utc, sha256, directory_policy, link_kind, health, error)
                VALUES ($generationId, $path, $normalizedPath, $identity, $size, $modified, $sha256, $policy, $link, $health, $error);
                SELECT last_insert_rowid();
                """,
                ("$generationId", generationId),
                ("$path", file.Path),
                ("$normalizedPath", file.NormalizedPath),
                ("$identity", file.PhysicalIdentity),
                ("$size", file.Size),
                ("$modified", file.ModifiedUtc.ToString("O", CultureInfo.InvariantCulture)),
                ("$sha256", file.Sha256.ToArray()),
                ("$policy", (int)file.DirectoryPolicy),
                ("$link", (int)file.LinkKind),
                ("$health", (int)file.Health),
                ("$error", file.Error));
            long fileId = Convert.ToInt64(await fileCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            foreach (string alias in file.PathAliases)
            {
                await Command(
                    "INSERT INTO font_path_alias(file_id, path, normalized_path) VALUES ($fileId, $path, $normalizedPath);",
                    ("$fileId", fileId),
                    ("$path", alias),
                    ("$normalizedPath", OperatingSystem.IsWindows() ? alias.ToUpperInvariant() : alias))
                    .ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            foreach (FontFaceRecord face in file.Faces)
            {
                SqliteCommand faceCommand = Command(
                    """
                    INSERT INTO font_face(file_id, face_index, strict_fingerprint, family_name, style_name, weight, width, slant, is_italic, is_fixed_pitch, technology, units_per_em, version, retention_score, is_runtime_preferred)
                    VALUES ($fileId, $index, $fingerprint, $family, $style, $weight, $width, $slant, $italic, $fixedPitch, $technology, $units, $version, $score, $preferred);
                    SELECT last_insert_rowid();
                    """,
                    ("$fileId", fileId),
                    ("$index", face.FaceIndex),
                    ("$fingerprint", face.StrictFingerprint.ToArray()),
                    ("$family", face.FamilyName),
                    ("$style", face.StyleName),
                    ("$weight", face.Weight),
                    ("$width", face.Width),
                    ("$slant", face.Slant),
                    ("$italic", face.IsItalic),
                    ("$fixedPitch", face.IsFixedPitch),
                    ("$technology", face.Technology),
                    ("$units", face.UnitsPerEm),
                    ("$version", face.Version),
                    ("$score", face.RetentionScore),
                    ("$preferred", face.IsRuntimePreferred));
                long faceId = Convert.ToInt64(await faceCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
                for (int ordinal = 0; ordinal < face.Names.Count; ordinal++)
                {
                    string name = face.Names[ordinal];
                    await Command(
                        "INSERT INTO face_name(face_id, ordinal, name, normalized_name) VALUES ($faceId, $ordinal, $name, $normalized);",
                        ("$faceId", faceId),
                        ("$ordinal", ordinal),
                        ("$name", name),
                        ("$normalized", name.ToUpperInvariant()))
                        .ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                if (face.UnicodeCoverage.Count > 0)
                {
                    await Command(
                        "INSERT INTO face_coverage(face_id, coverage_kind, compact_value) VALUES ($faceId, 1, $coverage);",
                        ("$faceId", faceId),
                        ("$coverage", UnicodeCoverageCodec.Encode(face.UnicodeCoverage)))
                        .ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await Command(
                    "INSERT INTO face_name_evidence(face_id, names_json) VALUES ($faceId, $json);",
                    ("$faceId", faceId),
                    ("$json", JsonSerializer.Serialize(face.NameRecords)))
                    .ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (SqliteCommand command in _commands.Values)
        {
            await command.DisposeAsync().ConfigureAwait(false);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "This private helper receives only the fixed inventory INSERT statements above; all values are bound parameters.")]
    private SqliteCommand Command(string sql, params (string Name, object? Value)[] values)
    {
        if (!_commands.TryGetValue(sql, out SqliteCommand? command))
        {
            command = _connection.CreateCommand();
            command.Transaction = _transaction;
            command.CommandText = sql;
            foreach (var value in values)
            {
                command.Parameters.AddWithValue(value.Name, value.Value ?? DBNull.Value);
            }

            command.Prepare();
            _commands.Add(sql, command);
        }
        else
        {
            for (int index = 0; index < values.Length; index++)
            {
                command.Parameters[index].Value = values[index].Value ?? DBNull.Value;
            }
        }

        return command;
    }
}
