using System;

namespace Jellyfin.Plugin.AssSubsetter.ScheduledTasks;

/// <summary>Stages selected for one scheduled font maintenance run.</summary>
[Flags]
public enum FontMaintenanceStages
{
    /// <summary>Skip every stage.</summary>
    None = 0,

    /// <summary>Refresh and analyze the inventory, then prune history.</summary>
    Analyze = 1,

    /// <summary>Quarantine verified duplicates and purge expired quarantine items.</summary>
    Quarantine = 2,

    /// <summary>Normalize eligible font filenames.</summary>
    Rename = 4
}
