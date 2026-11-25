using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
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
        AnalysisBatchRow? row = await PublishedCompleteAnalysisBatches(context)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return row is null ? null : (await LoadAnalysisRecordsAsync(context, [row], cancellationToken).ConfigureAwait(false))[0];
    }

    /// <inheritdoc/>
    public async Task<(AnalysisBatchSummary Summary, long EligibleQuarantineCount)?> GetLatestCompleteAnalysisSummaryAsync(CancellationToken cancellationToken)
    {
        FontDbContext context = await _database.OpenContextAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = context.ConfigureAwait(false);
        var transaction = await BeginReadSnapshotAsync(context, cancellationToken).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);
        AnalysisBatchSummary? summary = await SelectAnalysisSummaries(context, PublishedCompleteAnalysisBatches(context))
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (summary is null)
        {
            return null;
        }

        var candidateFiles = from duplicateGroup in context.Set<DuplicateGroupRow>()
                             join member in context.Set<GroupMemberRow>() on duplicateGroup.Id equals member.DuplicateGroupId
                             where duplicateGroup.AnalysisBatchId == summary.Id && member.Decision == (int)DuplicateGroupMemberDecision.QuarantineCandidate
                             select member.FileId;
        long count = await candidateFiles.Distinct().LongCountAsync(cancellationToken).ConfigureAwait(false);
        return (summary, count);
    }

    /// <inheritdoc/>
    public async Task<(long? GenerationId, long? LatestAnalysisId, FontAnalysisInputSnapshot? Input)> GetPublishedAnalysisStateAsync(
        bool includeInput, CancellationToken cancellationToken)
    {
        FontDbContext context = await _database.OpenContextAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = context.ConfigureAwait(false);
        var completeBatches = CompleteAnalysisBatches(context);
        var state = await context.Set<SchemaInfoRow>().Where(row => row.SingletonId == 1)
            .Select(schema => new
            {
                schema.PublishedGenerationId,
                LatestAnalysisId = completeBatches.Where(batch => batch.GenerationId == schema.PublishedGenerationId)
                    .Select(batch => (long?)batch.Id).FirstOrDefault(),
                InputJson = includeInput ? context.Set<ScanGenerationRow>()
                    .Where(generation => generation.Id == schema.PublishedGenerationId).Select(generation => generation.InputJson).FirstOrDefault() : null
            }).SingleAsync(cancellationToken).ConfigureAwait(false);
        return (state.PublishedGenerationId, state.LatestAnalysisId, state.InputJson is null
            ? null
            : JsonSerializer.Deserialize<FontAnalysisInputSnapshot>(state.InputJson));
    }

    private static IOrderedQueryable<AnalysisBatchRow> CompleteAnalysisBatches(FontDbContext context)
        => context.Set<AnalysisBatchRow>().Where(row => row.Status == (int)AnalysisBatchStatus.Complete)
            .OrderByDescending(row => row.CompletedUtc).ThenByDescending(row => row.Id);

    private static IQueryable<AnalysisBatchRow> PublishedCompleteAnalysisBatches(FontDbContext context)
        => CompleteAnalysisBatches(context)
            .Where(row => context.Set<SchemaInfoRow>().Any(schema => schema.SingletonId == 1 && schema.PublishedGenerationId == row.GenerationId));

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
        long count = await CountVisibleAnalysisBatchesAsync(context, cancellationToken).ConfigureAwait(false);
        List<AnalysisBatchRow> rows = await SelectAnalysisPage(context, offset, pageSize).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new FontRepositoryPage<AnalysisBatch>(await LoadAnalysisRecordsAsync(context, rows, cancellationToken).ConfigureAwait(false), count);
    }

    /// <inheritdoc/>
    public async Task<FontRepositoryPage<AnalysisBatchSummary>> LoadAnalysisSummaryPageAsync(long offset, int pageSize, CancellationToken cancellationToken)
    {
        ValidatePage(offset, pageSize);
        FontDbContext context = await _database.OpenContextAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = context.ConfigureAwait(false);
        var transaction = await BeginReadSnapshotAsync(context, cancellationToken).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);
        long count = await CountVisibleAnalysisBatchesAsync(context, cancellationToken).ConfigureAwait(false);
        List<AnalysisBatchSummary> summaries = await SelectAnalysisSummaries(context, SelectAnalysisPage(context, offset, pageSize))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new FontRepositoryPage<AnalysisBatchSummary>(summaries, count);
    }

    private static Task<long> CountVisibleAnalysisBatchesAsync(FontDbContext context, CancellationToken cancellationToken)
        => context.Set<AnalysisBatchRow>()
            .Where(row => context.Set<ScanGenerationRow>().Any(generation => generation.Id == row.GenerationId && generation.Status != 0))
            .LongCountAsync(cancellationToken);

    private static IQueryable<AnalysisBatchRow> SelectAnalysisPage(FontDbContext context, long offset, int pageSize)
    {
        // Page identifiers before loading report fields and bind SQLite's 64-bit OFFSET directly.
        var ids = context.Database.SqlQuery<long>($"""
            SELECT b.id AS Value FROM analysis_batch b
            JOIN scan_generation g ON g.id = b.generation_id
            WHERE g.status <> 0
            ORDER BY COALESCE(b.completed_utc, b.started_utc) DESC, b.id DESC
            LIMIT {pageSize} OFFSET {offset}
            """);
        return context.Set<AnalysisBatchRow>().Where(batch => ids.Contains(batch.Id))
            .OrderByDescending(batch => batch.CompletedUtc ?? batch.StartedUtc).ThenByDescending(batch => batch.Id);
    }

    private static IQueryable<AnalysisBatchSummary> SelectAnalysisSummaries(FontDbContext context, IQueryable<AnalysisBatchRow> batches)
        => batches.Select(batch => new AnalysisBatchSummary(
            batch.Id,
            batch.GenerationId,
            (AnalysisBatchStatus)batch.Status,
            batch.EvidenceVersion,
            ParseDate(batch.StartedUtc),
            ParseNullableDate(batch.CompletedUtc),
            batch.CandidateCount,
            batch.ComparisonCount,
            batch.ReclaimableBytes,
            context.Set<DuplicateGroupRow>().Count(group => group.AnalysisBatchId == batch.Id)));

    private static async Task<IReadOnlyList<AnalysisBatch>> LoadAnalysisRecordsAsync(FontDbContext context, List<AnalysisBatchRow> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        long[] ids = [.. rows.Select(row => row.Id)];
        List<FontRelationRow> relations = await context.Set<FontRelationRow>().Where(row => ids.Contains(row.AnalysisBatchId))
            .OrderBy(row => row.LeftFaceId).ThenBy(row => row.RightFaceId).ToListAsync(cancellationToken).ConfigureAwait(false);
        IQueryable<DuplicateGroupRow> groupsQuery = context.Set<DuplicateGroupRow>().Where(row => ids.Contains(row.AnalysisBatchId));
        List<DuplicateGroupRow> groups = await groupsQuery.OrderBy(row => row.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        // Start from the selected group IDs so SQLite can seek members instead of scanning the history.
        IQueryable<long> groupIds = groupsQuery.Select(group => group.Id);
        List<GroupMemberRow> members = await context.Set<GroupMemberRow>()
            .Where(row => groupIds.Contains(row.DuplicateGroupId))
            .OrderBy(row => row.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var relationsByBatch = relations.ToLookup(row => row.AnalysisBatchId);
        var groupsByBatch = groups.ToLookup(row => row.AnalysisBatchId);
        var membersByGroup = members.ToLookup(row => row.DuplicateGroupId);
        return
        [
            .. rows.Select(row => new AnalysisBatch
            {
                Id = row.Id,
                GenerationId = row.GenerationId,
                Status = (AnalysisBatchStatus)row.Status,
                EvidenceVersion = row.EvidenceVersion,
                StartedUtc = ParseDate(row.StartedUtc),
                CompletedUtc = ParseNullableDate(row.CompletedUtc),
                CandidateCount = row.CandidateCount,
                ComparisonCount = row.ComparisonCount,
                ReclaimableBytes = row.ReclaimableBytes,
                SummaryJson = row.SummaryJson,
                FaceScores = ReadFaceScores(row.SummaryJson, (AnalysisBatchStatus)row.Status),
                Relations =
                [
                    .. relationsByBatch[row.Id].Select(relation => new FontRelationRecord
                    {
                        Id = relation.Id,
                        LeftFaceId = relation.LeftFaceId,
                        RightFaceId = relation.RightFaceId,
                        RelationshipKind = (FontRelationshipKind)relation.RelationshipKind,
                        ReplacementDirection = (FontReplacementDirection)relation.ReplacementDirection,
                        EvidenceVersion = relation.EvidenceVersion,
                        EvidenceJson = relation.EvidenceJson
                    })
                ],
                Groups =
                [
                    .. groupsByBatch[row.Id].Select(group => new DuplicateGroupRecord
                    {
                        Id = group.Id,
                        RetainedFaceId = group.RetainedFaceId,
                        ReclaimableBytes = group.ReclaimableBytes,
                        EvidenceJson = group.EvidenceJson,
                        Members =
                        [
                            .. membersByGroup[group.Id].Select(member => new DuplicateGroupMemberRecord
                            {
                                Id = member.Id,
                                FaceId = member.FaceId,
                                FileId = member.FileId,
                                Decision = (DuplicateGroupMemberDecision)member.Decision,
                                RetentionScore = member.RetentionScore,
                                ScoreJson = member.ScoreJson,
                                Reason = member.Reason
                            })
                        ]
                    })
                ]
            })
        ];
    }

    private static void ValidatePage(long offset, int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, 200);
    }
}
