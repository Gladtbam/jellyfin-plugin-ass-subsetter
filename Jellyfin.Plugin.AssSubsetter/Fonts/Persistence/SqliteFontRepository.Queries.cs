using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using static Jellyfin.Plugin.AssSubsetter.Fonts.Persistence.FontDatabaseRows;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <summary>Database projections and bounded reads through the EF persistence model.</summary>
public sealed partial class SqliteFontRepository
{
    /// <inheritdoc/>
    public Task<IReadOnlyList<FontFileRecord>> LoadPublishedInventoryAsync(CancellationToken cancellationToken)
        => LoadInventoryCoreAsync(null, false, cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<FontFileRecord>> LoadPublishedInventoryForReuseAsync(CancellationToken cancellationToken)
        => LoadInventoryCoreAsync(null, true, cancellationToken);

    /// <inheritdoc/>
    public Task<IReadOnlyList<FontFileRecord>> LoadInventoryAsync(long generationId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generationId);
        return LoadInventoryCoreAsync(generationId, false, cancellationToken);
    }

    private async Task<IReadOnlyList<FontFileRecord>> LoadInventoryCoreAsync(long? generationId, bool includeCoverage, CancellationToken cancellationToken)
    {
        FontDbContext context = await _database.OpenContextAsync(cancellationToken).ConfigureAwait(false);
        await using var contextScope = context.ConfigureAwait(false);
        SqliteTransaction transaction = await BeginReadSnapshotAsync(context, cancellationToken).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);
        long? generation = generationId ?? await PublishedGenerationAsync(context, cancellationToken).ConfigureAwait(false);
        IQueryable<FontFileRow> filesQuery = context.Set<FontFileRow>().Where(file => file.GenerationId == generation);
        List<FontFileRow> files = await filesQuery.OrderBy(file => file.NormalizedPath).ToListAsync(cancellationToken).ConfigureAwait(false);
        IQueryable<FontFaceRow> facesQuery = context.Set<FontFaceRow>().Where(face => filesQuery.Select(file => file.Id).Contains(face.FileId));
        List<FontFaceRow> faces = await facesQuery.OrderBy(face => face.FaceIndex).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<FontPathAliasRow> aliases = await context.Set<FontPathAliasRow>()
            .Where(alias => filesQuery.Select(file => file.Id).Contains(alias.FileId)).OrderBy(alias => alias.NormalizedPath)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var metadata = await LoadFaceMetadataAsync(context, facesQuery.Select(face => face.Id), includeCoverage, cancellationToken).ConfigureAwait(false);
        ILookup<long, FontFaceRow> facesByFile = faces.ToLookup(face => face.FileId);
        ILookup<long, FontPathAliasRow> aliasesByFile = aliases.ToLookup(alias => alias.FileId);
        FontFileRecord[] result = files.Select(file => new FontFileRecord
        {
            Id = file.Id, GenerationId = file.GenerationId, Path = file.Path, NormalizedPath = file.NormalizedPath,
            PhysicalIdentity = file.PhysicalIdentity, PathAliases = aliasesByFile[file.Id].Select(alias => alias.Path).ToArray(),
            Size = file.Size, ModifiedUtc = ParseDate(file.ModifiedUtc), Sha256 = file.Sha256,
            DirectoryPolicy = (FontDirectoryPolicy)file.DirectoryPolicy, LinkKind = (FontLinkKind)file.LinkKind,
            Health = (FontFileHealth)file.Health, Error = file.Error,
            Faces = facesByFile[file.Id].Select(face => new FontFaceRecord
            {
                Id = face.Id, FaceIndex = face.FaceIndex, StrictFingerprint = face.StrictFingerprint,
                FamilyName = face.FamilyName, StyleName = face.StyleName, Weight = face.Weight, Width = face.Width,
                Slant = face.Slant, IsItalic = face.IsItalic, IsFixedPitch = face.IsFixedPitch,
                Technology = face.Technology, UnitsPerEm = face.UnitsPerEm, Version = face.Version,
                RetentionScore = face.RetentionScore, IsRuntimePreferred = face.IsRuntimePreferred,
                Names = metadata.Names[face.Id].Select(name => name.Name).ToArray(),
                NameRecords = metadata.Evidence.GetValueOrDefault(face.Id) ?? [],
                UnicodeCoverage = metadata.Coverage.GetValueOrDefault(face.Id) ?? []
            }).ToArray()
        }).ToArray();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<IReadOnlyList<RuntimeFontFace>> LoadRuntimeFacesCoreAsync(long? generationId, CancellationToken cancellationToken)
    {
        FontDbContext context = await _database.OpenContextAsync(cancellationToken).ConfigureAwait(false);
        await using var contextScope = context.ConfigureAwait(false);
        SqliteTransaction transaction = await BeginReadSnapshotAsync(context, cancellationToken).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);
        long? generation = generationId ?? await PublishedGenerationAsync(context, cancellationToken).ConfigureAwait(false);
        IQueryable<long> faceIds = from file in context.Set<FontFileRow>()
                                   join face in context.Set<FontFaceRow>() on file.Id equals face.FileId
                                   where file.GenerationId == generation
                                   select face.Id;
        var names = await context.Set<FaceNameRow>().Where(name => faceIds.Contains(name.FaceId)).OrderBy(name => name.Ordinal)
            .Select(name => new { name.FaceId, name.Name }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var namesByFace = names.ToLookup(name => name.FaceId, name => name.Name);
        // Stream the joined projection and build each runtime face once, avoiding metadata dictionaries and record copies.
        var query = from file in context.Set<FontFileRow>()
                    join face in context.Set<FontFaceRow>() on file.Id equals face.FileId
                    join evidence in context.Set<FaceNameEvidenceRow>() on face.Id equals evidence.FaceId into evidenceRows
                    from evidence in evidenceRows.DefaultIfEmpty()
                    join coverage in context.Set<FaceCoverageRow>().Where(row => row.CoverageKind == 1) on face.Id equals coverage.FaceId into coverageRows
                    from coverage in coverageRows.DefaultIfEmpty()
                    where file.GenerationId == generation
                    orderby file.NormalizedPath, face.FaceIndex
                    select new
                    {
                        face.Id, file.Path, file.Sha256, face.FaceIndex, face.FamilyName, face.StyleName, face.Weight, face.Width, face.Slant,
                        face.IsItalic, face.IsFixedPitch, face.Technology, face.RetentionScore, face.IsRuntimePreferred,
                        NamesJson = evidence == null ? null : evidence.NamesJson,
                        Coverage = coverage == null ? null : coverage.CompactValue
                    };
        var faces = new List<RuntimeFontFace>();
        await foreach (var face in query.AsAsyncEnumerable().WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            faces.Add(new RuntimeFontFace(face.Id, face.Path, face.FaceIndex, face.FamilyName, face.StyleName, namesByFace[face.Id].ToArray(), face.Weight, face.Width, face.Slant, face.IsItalic, face.IsFixedPitch, face.Technology, face.RetentionScore, face.IsRuntimePreferred)
            {
                ContentHash = Convert.ToHexString(face.Sha256),
                NameRecords = face.NamesJson is null ? [] : JsonSerializer.Deserialize<OpenTypeNameRecord[]>(face.NamesJson) ?? [],
                UnicodeCoverage = face.Coverage is null ? [] : UnicodeCoverageCodec.Decode(face.Coverage)
            });
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return faces;
    }

    private static async Task<(ILookup<long, FaceNameRow> Names, Dictionary<long, IReadOnlyList<OpenTypeNameRecord>> Evidence, Dictionary<long, IReadOnlyList<UnicodeRange>> Coverage)>
        LoadFaceMetadataAsync(FontDbContext context, IQueryable<long> faceIds, bool includeCoverage, CancellationToken cancellationToken)
    {
        List<FaceNameRow> names = await context.Set<FaceNameRow>().Where(name => faceIds.Contains(name.FaceId))
            .OrderBy(name => name.Ordinal).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<FaceNameEvidenceRow> evidence = await context.Set<FaceNameEvidenceRow>().Where(row => faceIds.Contains(row.FaceId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var coverage = new Dictionary<long, IReadOnlyList<UnicodeRange>>();
        if (includeCoverage)
        {
            List<FaceCoverageRow> rows = await context.Set<FaceCoverageRow>().Where(row => row.CoverageKind == 1 && faceIds.Contains(row.FaceId))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (FaceCoverageRow row in rows)
            {
                coverage.Add(row.FaceId, UnicodeCoverageCodec.Decode(row.CompactValue));
            }
        }

        return (names.ToLookup(row => row.FaceId),
            evidence.ToDictionary(row => row.FaceId, row => (IReadOnlyList<OpenTypeNameRecord>)(JsonSerializer.Deserialize<OpenTypeNameRecord[]>(row.NamesJson) ?? [])),
            coverage);
    }

    private static Task<long?> PublishedGenerationAsync(FontDbContext context, CancellationToken cancellationToken)
        => context.Set<SchemaInfoRow>().Where(row => row.SingletonId == 1).Select(row => row.PublishedGenerationId).SingleAsync(cancellationToken);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1849:Call async methods when in an async method", Justification = "SQLite exposes the deferred flag only on the synchronous overload; the async overload acquires the writer lock.")]
    private static async Task<SqliteTransaction> BeginReadSnapshotAsync(FontDbContext context, CancellationToken cancellationToken)
    {
        // Deferred transactions pin the first SELECT's WAL snapshot without acquiring the writer lock.
        SqliteConnection connection = (SqliteConnection)context.Database.GetDbConnection();
        cancellationToken.ThrowIfCancellationRequested();
        SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
        try
        {
            await context.Database.UseTransactionAsync(transaction, cancellationToken).ConfigureAwait(false);
            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
