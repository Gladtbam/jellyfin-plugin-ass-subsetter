namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <summary>Provides the durable queue, candidate lifecycle, read views and retention needed by maintenance task execution.</summary>
public interface IFontMaintenanceExecutionRepository : IFontManagementReader, IFontOperationRepository, IFontRenameRepository, IFontRetentionRepository
{
}
