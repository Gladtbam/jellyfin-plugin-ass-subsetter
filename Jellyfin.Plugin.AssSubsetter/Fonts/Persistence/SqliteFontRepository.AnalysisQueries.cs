using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Microsoft.EntityFrameworkCore;
using static Jellyfin.Plugin.AssSubsetter.Fonts.Persistence.FontDatabaseRows;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <summary>Analysis projections loaded within one catalog read snapshot.</summary>
public sealed partial class SqliteFontRepository
{
    /// <inheritdoc/>
    public async Task<AnalysisBatch?> GetLatestCompleteAnalysisAsync(CancellationToken cancellationToken)
    {
        FontDbContext context = await _database.OpenContextAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = context.ConfigureAwait(false);
        var transaction = await BeginReadSnapshotAsync(context, cancellationToken).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);
        AnalysisBatchRow? row = await context.Set<AnalysisBatchRow>()
            .Where(row => row.Status == (int)AnalysisBatchStatus.Complete
                && context.Set<SchemaInfoRow>().Any(schema => schema.SingletonId == 1 && schema.PublishedGenerationId == row.GenerationId))
            .OrderByDescending(row => row.CompletedUtc).ThenByDescending(row => row.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return row is null ? null : (await LoadAnalysisRecordsAsync(context, [row], cancellationToken).ConfigureAwait(false))[0];
    }

    /// <inheritdoc/>
    public async Task<AnalysisBatch?> GetAnalysisBatchAsync(long batchId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchId);
        FontDbContext context = await _database.OpenContextAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = context.ConfigureAwait(false);
        var transaction = await BeginReadSnapshotAsync(context, cancellationToken).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);
        AnalysisBatchRow? row = await context.Set<AnalysisBatchRow>().SingleOrDefaultAsync(row => row.Id == batchId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : (await LoadAnalysisRecordsAsync(context, [row], cancellationToken).ConfigureAwait(false))[0];
    }

    /// <inheritdoc/>
    public async Task<FontRepositoryPage<AnalysisBatch>> LoadAnalysisBatchPageAsync(long offset, int pageSize, CancellationToken cancellationToken)
    {
        ValidatePage(offset, pageSize);
        FontDbContext context = await _database.OpenContextAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = context.ConfigureAwait(false);
        var transaction = await BeginReadSnapshotAsync(context, cancellationToken).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);
        long count = await context.Set<AnalysisBatchRow>()
            .Where(row => context.Set<ScanGenerationRow>().Any(generation => generation.Id == row.GenerationId && generation.Status != 0))
            .LongCountAsync(cancellationToken).ConfigureAwait(false);
        // IFontRepository accepts a 64-bit offset; retain SQLite OFFSET rather than narrow it to LINQ Skip(int).
        List<AnalysisBatchRow> rows = await context.Set<AnalysisBatchRow>().FromSqlInterpolated($"""
            SELECT b.* FROM analysis_batch b
            JOIN scan_generation g ON g.id = b.generation_id
            WHERE g.status <> 0
            ORDER BY COALESCE(b.completed_utc, b.started_utc) DESC, b.id DESC
            LIMIT {pageSize} OFFSET {offset}
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new FontRepositoryPage<AnalysisBatch>(await LoadAnalysisRecordsAsync(context, rows, cancellationToken).ConfigureAwait(false), count);
    }

    private static async Task<IReadOnlyList<AnalysisBatch>> LoadAnalysisRecordsAsync(FontDbContext context, List<AnalysisBatchRow> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        long[] ids = rows.Select(row => row.Id).ToArray();
        List<FontRelationRow> relations = await context.Set<FontRelationRow>().Where(row => ids.Contains(row.AnalysisBatchId))
            .OrderBy(row => row.LeftFaceId).ThenBy(row => row.RightFaceId).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<DuplicateGroupRow> groups = await context.Set<DuplicateGroupRow>().Where(row => ids.Contains(row.AnalysisBatchId))
            .OrderBy(row => row.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        List<GroupMemberRow> members = await context.Set<GroupMemberRow>()
            .Where(row => context.Set<DuplicateGroupRow>().Any(group => group.Id == row.DuplicateGroupId && ids.Contains(group.AnalysisBatchId)))
            .OrderBy(row => row.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var relationsByBatch = relations.ToLookup(row => row.AnalysisBatchId);
        var groupsByBatch = groups.ToLookup(row => row.AnalysisBatchId);
        var membersByGroup = members.ToLookup(row => row.DuplicateGroupId);
        return rows.Select(row => new AnalysisBatch
        {
            Id = row.Id, GenerationId = row.GenerationId, Status = (AnalysisBatchStatus)row.Status,
            EvidenceVersion = row.EvidenceVersion, StartedUtc = ParseDate(row.StartedUtc), CompletedUtc = ParseNullableDate(row.CompletedUtc),
            CandidateCount = row.CandidateCount, ComparisonCount = row.ComparisonCount, ReclaimableBytes = row.ReclaimableBytes,
            SummaryJson = row.SummaryJson, FaceScores = ReadFaceScores(row.SummaryJson, (AnalysisBatchStatus)row.Status),
            Relations = relationsByBatch[row.Id].Select(relation => new FontRelationRecord
            {
                Id = relation.Id, LeftFaceId = relation.LeftFaceId, RightFaceId = relation.RightFaceId,
                RelationshipKind = (FontRelationshipKind)relation.RelationshipKind, ReplacementDirection = (FontReplacementDirection)relation.ReplacementDirection,
                EvidenceVersion = relation.EvidenceVersion, EvidenceJson = relation.EvidenceJson
            }).ToArray(),
            Groups = groupsByBatch[row.Id].Select(group => new DuplicateGroupRecord
            {
                Id = group.Id, RetainedFaceId = group.RetainedFaceId, ReclaimableBytes = group.ReclaimableBytes, EvidenceJson = group.EvidenceJson,
                Members = membersByGroup[group.Id].Select(member => new DuplicateGroupMemberRecord
                {
                    Id = member.Id, FaceId = member.FaceId, FileId = member.FileId, Decision = (DuplicateGroupMemberDecision)member.Decision,
                    RetentionScore = member.RetentionScore, ScoreJson = member.ScoreJson, Reason = member.Reason
                }).ToArray()
            }).ToArray()
        }).ToArray();
    }

    private static void ValidatePage(long offset, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 200);
    }
}
