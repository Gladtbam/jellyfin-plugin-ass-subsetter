namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <summary>Provides read-only inventory, report, operation, rename and health views for administration.</summary>
public interface IFontManagementReader : IFontInventoryReader, IFontAnalysisReader, IFontMaintenanceReader, IFontDiagnosticsReader, IFontOperationReader, IFontRenameReader
{
}
