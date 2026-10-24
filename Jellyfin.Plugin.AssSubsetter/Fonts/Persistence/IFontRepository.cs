namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <summary>
/// Compatibility aggregate for the shared font store. Consumers should request the capabilities they need.
/// All capabilities are backed by the same repository instance; analysis publication remains one atomic operation.
/// </summary>
public interface IFontRepository :
    IFontRepositoryLifecycle,
    IFontInventoryWriter,
    IFontRuntimeFaceReader,
    IFontAnalysisPublisher,
    IFontAnalysisEvidenceRepository,
    IFontMaintenanceRepository,
    IFontDiagnosticsWriter,
    IFontMaintenanceExecutionRepository
{
}
