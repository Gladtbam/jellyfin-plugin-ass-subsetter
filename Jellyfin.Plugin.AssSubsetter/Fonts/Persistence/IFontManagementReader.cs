using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <summary>Provides read-only inventory, report, operation, rename and health views for administration.</summary>
public interface IFontManagementReader : IFontInventoryReader, IFontAnalysisReader, IFontMaintenanceReader, IFontDiagnosticsReader, IFontOperationReader, IFontRenameReader
{
    /// <summary>Counts the files and Faces in the published inventory.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The file and Face totals from one database snapshot.</returns>
    Task<(long FileCount, long FaceCount)> GetPublishedInventoryCountsAsync(CancellationToken cancellationToken);

    /// <summary>Reads the published generation and its current analysis identity.</summary>
    /// <param name="includeInput">Whether to include the inventory's saved analysis input.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The generation, latest completed analysis identifier and optional saved input from one database snapshot.</returns>
    Task<(long? GenerationId, long? LatestAnalysisId, FontAnalysisInputSnapshot? Input)> GetPublishedAnalysisStateAsync(bool includeInput, CancellationToken cancellationToken);

    /// <summary>Reads the latest complete report's summary and counts distinct candidate files.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The summary and candidate count, or null before a completed publication.</returns>
    Task<(AnalysisBatchSummary Summary, long EligibleQuarantineCount)?> GetLatestCompleteAnalysisSummaryAsync(CancellationToken cancellationToken);

    /// <summary>Reads a page of analysis summaries and its total count.</summary>
    /// <param name="offset">The zero-based row offset.</param>
    /// <param name="pageSize">The bounded number of reports to load.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The requested page and total count.</returns>
    Task<FontRepositoryPage<AnalysisBatchSummary>> LoadAnalysisSummaryPageAsync(long offset, int pageSize, CancellationToken cancellationToken);

    /// <summary>Loads one page of duplicate groups and their members.</summary>
    /// <param name="analysisBatchId">The owning report, or null for the latest complete published report.</param>
    /// <param name="offset">The zero-based row offset.</param>
    /// <param name="pageSize">The maximum number of groups.</param>
    /// <param name="decision">An optional member decision that each group must contain.</param>
    /// <param name="sortByReclaimableBytes">Whether to sort by reclaimable bytes descending before group identifier.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The page and exact filtered group count.</returns>
    Task<FontRepositoryPage<DuplicateGroupSummary>> LoadDuplicateGroupPageAsync(
        long? analysisBatchId, long offset, int pageSize, DuplicateGroupMemberDecision? decision, bool sortByReclaimableBytes, CancellationToken cancellationToken);

    /// <summary>Loads one group's displayed members, their files, and their pair relations.</summary>
    /// <param name="analysisBatchId">The owning report identifier.</param>
    /// <param name="groupId">The persisted group identifier.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The group detail, or null if the group does not belong to the report.</returns>
    Task<(DuplicateGroupSummary Group, IReadOnlyList<FontFileRecord> Files, IReadOnlyList<FontRelationRecord> Relations)?> GetDuplicateGroupDetailAsync(
        long analysisBatchId, long groupId, CancellationToken cancellationToken);
}
