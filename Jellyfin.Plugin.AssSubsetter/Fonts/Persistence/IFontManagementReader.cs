using System.Threading;
using System.Threading.Tasks;
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
}
