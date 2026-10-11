namespace Jellyfin.Plugin.AssSubsetter.ScheduledTasks;

/// <summary>Counts durable history rows removed by the daily maintenance task.</summary>
public sealed record FontHistoryPruneResult(int DeletedBatches, int DeletedGenerations, bool Skipped);
