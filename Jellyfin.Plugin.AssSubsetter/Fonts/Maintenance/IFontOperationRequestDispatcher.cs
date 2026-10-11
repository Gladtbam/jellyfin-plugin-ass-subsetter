namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

/// <summary>Wakes scheduling of persisted font requests after authorization has been saved.</summary>
public interface IFontOperationRequestDispatcher
{
    /// <summary>Requests another scan of the durable pending queue.</summary>
    void Signal();
}
