using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Microsoft.EntityFrameworkCore;
using static Jellyfin.Plugin.AssSubsetter.Fonts.Persistence.FontDatabaseRows;
using static Jellyfin.Plugin.AssSubsetter.Fonts.Persistence.FontReportSnapshot;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

public sealed partial class SqliteFontRepository
{
    async Task<TResult> IFontManagementReader.ReadReportAsync<TResult>(
        long? analysisBatchId, Func<FontReportSnapshot?, Task<TResult>> readPage, CancellationToken cancellationToken)
    {
        FontDbContext context = await _database.OpenContextAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = context.ConfigureAwait(false);
        var transaction = await BeginReadSnapshotAsync(context, cancellationToken).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);

        var batches = analysisBatchId is long id
            ? context.Set<AnalysisBatchRow>().Where(batch => batch.Id == id)
            : PublishedCompleteAnalysisBatches(context);
        var batch = await batches.Select(row => new { row.Id, row.GenerationId })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (batch is null)
        {
            return await readPage(null).ConfigureAwait(false);
        }

        var relations = context.Set<FontRelationRow>().Where(row => row.AnalysisBatchId == batch.Id);
        var relationMetadata = await relations.OrderBy(row => row.LeftFaceId).ThenBy(row => row.RightFaceId)
            .Select(row => new FontReportRelation(
                row.Id, row.LeftFaceId, row.RightFaceId, (FontRelationshipKind)row.RelationshipKind, (FontReplacementDirection)row.ReplacementDirection))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var groups = context.Set<DuplicateGroupRow>().Where(row => row.AnalysisBatchId == batch.Id);
        var groupMetadata = await groups.OrderBy(row => row.Id).Select(row => new { row.Id, row.ReclaimableBytes })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var members = context.Set<GroupMemberRow>().Where(row => groups.Select(group => group.Id).Contains(row.DuplicateGroupId));
        var memberMetadata = await members.OrderBy(row => row.Id)
            .Select(row => new
            {
                row.DuplicateGroupId,
                Member = new FontReportMember(row.FaceId, row.FileId, (DuplicateGroupMemberDecision)row.Decision, row.Reason)
            }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var membersByGroup = memberMetadata.ToLookup(row => row.DuplicateGroupId, row => row.Member);

        var faces = context.Set<FontFaceRow>();
        var relatedFaceIds = relations.Select(row => row.LeftFaceId).Union(relations.Select(row => row.RightFaceId));
        var relatedFileIds = faces.Where(face => relatedFaceIds.Contains(face.Id)).Select(face => face.FileId)
            .Union(members.Select(member => member.FileId));
        var files = context.Set<FontFileRow>().Where(file => file.GenerationId == batch.GenerationId
            && (file.Health == (int)FontFileHealth.Unclassifiable || relatedFileIds.Contains(file.Id)));
        var inventory = await ReadReportFilesAsync(files, faces, cancellationToken).ConfigureAwait(false);

        return await readPage(new FontReportSnapshot(
            batch.Id,
            relationMetadata,
            [.. groupMetadata.Select(group => new FontReportGroup(group.Id, group.ReclaimableBytes, [.. membersByGroup[group.Id]]))],
            inventory,
            (relationIds, scoreFaceIds, token) => ReadReportEvidenceAsync(context, batch.Id, batch.GenerationId, relationIds, scoreFaceIds, token)))
            .ConfigureAwait(false);
    }

    private static async Task<FontReportFile[]> ReadReportFilesAsync(
        IQueryable<FontFileRow> files, IQueryable<FontFaceRow> faces, CancellationToken cancellationToken)
    {
        // Include every sibling Face for whole-file replacement checks, even if it has no relation or group membership.
        var query = from file in files
                    join face in faces on file.Id equals face.FileId into fileFaces
                    from face in fileFaces.DefaultIfEmpty()
                    orderby file.NormalizedPath, face.FaceIndex
                    select new
                    {
                        file.Id,
                        file.Path,
                        file.Health,
                        Error = file.Health == (int)FontFileHealth.Unclassifiable ? file.Error : null,
                        FaceId = (long?)face.Id,
                        face.Technology
                    };
        var rows = await query.ToListAsync(cancellationToken).ConfigureAwait(false);
        return
        [
            .. rows.GroupBy(row => (row.Id, row.Path, row.Health, row.Error)).Select(file => new FontReportFile(
                file.Key.Id,
                file.Key.Path,
                (FontFileHealth)file.Key.Health,
                file.Key.Error,
                [.. file.Where(row => row.FaceId.HasValue).Select(row => new FontReportFace(row.FaceId!.Value, row.Technology))]))
        ];
    }

    private static async Task<FontReportEvidence> ReadReportEvidenceAsync(
        FontDbContext context, long batchId, long generationId, long[] relationIds, long[] scoreFaceIds, CancellationToken cancellationToken)
    {
        FontRelationRecord[] relations = [];
        FontFaceRecord[] faces = [];
        if (relationIds.Length > 0)
        {
            var relationRows = await context.Set<FontRelationRow>()
                .Where(row => row.AnalysisBatchId == batchId && relationIds.Contains(row.Id))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            relations = [.. relationRows.Select(ToRelation)];
            long[] pairFaceIds = [.. relations.Select(row => row.LeftFaceId).Concat(relations.Select(row => row.RightFaceId)).Distinct()];
            var faceQuery = from face in context.Set<FontFaceRow>()
                            join file in context.Set<FontFileRow>() on face.FileId equals file.Id
                            where pairFaceIds.Contains(face.Id) && file.GenerationId == generationId
                            select face;
            var faceRows = await faceQuery.ToListAsync(cancellationToken).ConfigureAwait(false);
            var metadata = await LoadFaceMetadataAsync(context, faceQuery.Select(face => face.Id), false, cancellationToken).ConfigureAwait(false);
            faces =
            [
                .. faceRows.Select(face => new FontFaceRecord
                {
                    Id = face.Id, FaceIndex = face.FaceIndex, StrictFingerprint = face.StrictFingerprint,
                    FamilyName = face.FamilyName, StyleName = face.StyleName, Weight = face.Weight, Width = face.Width,
                    Slant = face.Slant, IsItalic = face.IsItalic, IsFixedPitch = face.IsFixedPitch, Technology = face.Technology,
                    UnitsPerEm = face.UnitsPerEm, Version = face.Version, RetentionScore = face.RetentionScore,
                    IsRuntimePreferred = face.IsRuntimePreferred,
                    Names = [.. metadata.Names[face.Id].Select(name => name.Name)],
                    NameRecords = metadata.Evidence.GetValueOrDefault(face.Id) ?? []
                })
            ];
        }

        FontFaceAnalysisRecord[] scores = scoreFaceIds.Length == 0
            ? []
            : await ReadReportScoresAsync(context, batchId, scoreFaceIds, cancellationToken).ConfigureAwait(false);
        return new FontReportEvidence(relations, faces, scores);
    }

    private static async Task<FontFaceAnalysisRecord[]> ReadReportScoresAsync(
        FontDbContext context, long batchId, long[] faceIds, CancellationToken cancellationToken)
    {
        // Match the case-insensitive score properties and numeric strings accepted by the persisted summary reader.
        // Keep the summary document in SQLite and return only the requested score objects.
        string faceIdsJson = JsonSerializer.Serialize(faceIds);
        var rows = await context.Database.SqlQuery<string>($"""
            SELECT score.value AS Value
            FROM analysis_batch AS batch
            CROSS JOIN json_each(batch.summary_json, '$.faceScores') AS score
            WHERE batch.id = {batchId} AND CAST((
                SELECT property.atom
                FROM json_each(score.value) AS property
                WHERE lower(property.key) = 'faceid'
                ORDER BY property.id DESC
                LIMIT 1
            ) AS INTEGER) IN (SELECT value FROM json_each({faceIdsJson}))
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(row => JsonSerializer.Deserialize<FontFaceAnalysisRecord>(row, JsonSerializerOptions.Web)!)];
    }
}
