namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

/// <summary>Wakes the plugin-owned worker after an explicit restore request is persisted.</summary>
public interface IFontRestoreRequestDispatcher
{
    /// <summary>Signals that restore work may be pending.</summary>
    void Signal();
}
