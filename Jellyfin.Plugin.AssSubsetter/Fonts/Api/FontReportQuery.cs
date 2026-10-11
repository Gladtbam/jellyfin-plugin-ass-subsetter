namespace Jellyfin.Plugin.AssSubsetter.Fonts.Api;

/// <summary>Specifies one safe pageable analysis-report query.</summary>
/// <param name="Page">The requested one-based page number.</param>
/// <param name="PageSize">The requested page size.</param>
/// <param name="AnalysisBatchId">The optional persisted analysis batch identifier.</param>
/// <param name="Relationship">The optional allowlisted relationship name.</param>
/// <param name="Unclassifiable">The optional all, only, or exclude unclassifiable-state filter.</param>
/// <param name="Directory">The optional exact persisted directory.</param>
/// <param name="Technology">The optional allowlisted font technology.</param>
/// <param name="Action">The optional allowlisted report action.</param>
public sealed record FontReportQuery(int Page, int PageSize, long? AnalysisBatchId, string? Relationship, string? Unclassifiable, string? Directory, string? Technology, string? Action);
