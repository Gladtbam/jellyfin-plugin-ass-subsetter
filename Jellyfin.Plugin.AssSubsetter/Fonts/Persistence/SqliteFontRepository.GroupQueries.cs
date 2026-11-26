using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Microsoft.EntityFrameworkCore;
using static Jellyfin.Plugin.AssSubsetter.Fonts.Persistence.FontDatabaseRows;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <content>Reads bounded duplicate-group projections for the management API.</content>
public sealed partial class SqliteFontRepository
{
    /// <summary>Loads one page of duplicate groups and their members.</summary>
    /// <param name="analysisBatchId">The owning report, or null for the latest complete published report.</param>
    /// <param name="offset">The zero-based row offset.</param>
    /// <param name="pageSize">The maximum number of groups.</param>
    /// <param name="decision">An optional member decision that each group must contain.</param>
    /// <param name="sortByReclaimableBytes">Whether to sort by reclaimable bytes descending before group identifier.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The page and exact filtered group count.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The supplied report identifier is nonpositive, the offset is negative, or the page size is outside 1 through 200.</exception>
    public async Task<FontRepositoryPage<DuplicateGroupSummary>> LoadDuplicateGroupPageAsync(
        long? analysisBatchId, long offset, int pageSize, DuplicateGroupMemberDecision? decision, bool sortByReclaimableBytes, CancellationToken cancellationToken)
    {
        if (analysisBatchId is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(analysisBatchId));
        }

        ValidatePage(offset, pageSize);
        FontDbContext context = await _database.OpenContextAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = context.ConfigureAwait(false);
        var transaction = await BeginReadSnapshotAsync(context, cancellationToken).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);

        long? batchId = analysisBatchId ?? await PublishedCompleteAnalysisBatches(context)
            .Select(batch => (long?)batch.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (batchId is null)
        {
            return new([], 0);
        }

        int? decisionCode = (int?)decision;
        var matchingGroups = context.Set<DuplicateGroupRow>().Where(row => row.AnalysisBatchId == batchId);
        if (decisionCode.HasValue)
        {
            matchingGroups = matchingGroups.Where(row => context.Set<GroupMemberRow>()
                .Any(member => member.DuplicateGroupId == row.Id && member.Decision == decisionCode.Value));
        }

        long count = await matchingGroups.LongCountAsync(cancellationToken).ConfigureAwait(false);
        if (offset >= count)
        {
            return new([], count);
        }

        // Page group IDs before reading any member scores and bind the 64-bit offset directly.
        var pageIds = context.Database.SqlQuery<long>($"""
            SELECT g.id AS Value FROM duplicate_group g
            WHERE g.analysis_batch_id = {batchId}
              AND ({decisionCode} IS NULL OR EXISTS (
                  SELECT 1 FROM group_member m WHERE m.duplicate_group_id = g.id AND m.decision = {decisionCode}
              ))
            ORDER BY CASE WHEN {sortByReclaimableBytes} THEN g.reclaimable_bytes ELSE 0 END DESC, g.id
            LIMIT {pageSize} OFFSET {offset}
            """);
        var pageQuery = context.Set<DuplicateGroupRow>().Where(row => pageIds.Contains(row.Id));
        var orderedPage = sortByReclaimableBytes
            ? pageQuery.OrderByDescending(row => row.ReclaimableBytes).ThenBy(row => row.Id)
            : pageQuery.OrderBy(row => row.Id);
        var page = await orderedPage.Select(row => new { row.Id, row.AnalysisBatchId, row.RetainedFaceId, row.ReclaimableBytes })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        long[] ids = [.. page.Select(row => row.Id)];
        var members = await context.Set<GroupMemberRow>().Where(row => ids.Contains(row.DuplicateGroupId))
            .OrderBy(row => row.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var membersByGroup = members.ToLookup(row => row.DuplicateGroupId, ToGroupMember);
        return new(
            [.. page.Select(row => new DuplicateGroupSummary(row.AnalysisBatchId, row.Id, row.RetainedFaceId, row.ReclaimableBytes, [.. membersByGroup[row.Id]]))],
            count);
    }

    /// <summary>Loads one group's displayed members, their files, and their pair relations.</summary>
    /// <param name="analysisBatchId">The owning report identifier.</param>
    /// <param name="groupId">The persisted group identifier.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The group detail, or null if the group does not belong to the report.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="analysisBatchId"/> is zero or negative.</exception>
    public async Task<(DuplicateGroupSummary Group, IReadOnlyList<FontFileRecord> Files, IReadOnlyList<FontRelationRecord> Relations)?> GetDuplicateGroupDetailAsync(
        long analysisBatchId, long groupId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(analysisBatchId);
        FontDbContext context = await _database.OpenContextAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = context.ConfigureAwait(false);
        var transaction = await BeginReadSnapshotAsync(context, cancellationToken).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);
        var header = await (
            from duplicateGroup in context.Set<DuplicateGroupRow>()
            join batch in context.Set<AnalysisBatchRow>() on duplicateGroup.AnalysisBatchId equals batch.Id
            where batch.Id == analysisBatchId && duplicateGroup.Id == groupId
            select new { batch.GenerationId, duplicateGroup.RetainedFaceId, duplicateGroup.ReclaimableBytes })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (header is null)
        {
            return null;
        }

        var groupMembers = context.Set<GroupMemberRow>().Where(row => row.DuplicateGroupId == groupId);
        var members = await groupMembers.OrderBy(row => row.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        var summary = new DuplicateGroupSummary(analysisBatchId, groupId, header.RetainedFaceId, header.ReclaimableBytes, [.. members.Select(ToGroupMember)]);
        if (members.Count == 0)
        {
            return (summary, [], []);
        }

        var relations = await context.Set<FontRelationRow>()
            .Where(row => row.AnalysisBatchId == analysisBatchId
                && groupMembers.Select(member => member.FaceId).Contains(row.LeftFaceId)
                && groupMembers.Any(member => member.FaceId == row.RightFaceId))
            .OrderBy(row => row.LeftFaceId).ThenBy(row => row.RightFaceId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        // Read all Faces in each member's physical file to preserve collection impact calculations.
        var fileIds = from member in groupMembers
                      join face in context.Set<FontFaceRow>() on member.FaceId equals face.Id
                      select face.FileId;
        var files = await ReadInventoryAsync(
            context,
            context.Set<FontFileRow>().Where(file => file.GenerationId == header.GenerationId && fileIds.Contains(file.Id)),
            false,
            cancellationToken).ConfigureAwait(false);
        return (summary, files, [.. relations.Select(ToRelation)]);
    }
}
