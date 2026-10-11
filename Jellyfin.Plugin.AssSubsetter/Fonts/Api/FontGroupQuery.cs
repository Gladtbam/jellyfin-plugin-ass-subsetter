namespace Jellyfin.Plugin.AssSubsetter.Fonts.Api;

/// <summary>Specifies a safe pageable duplicate-group query.</summary>
/// <param name="Page">The requested one-based page number.</param>
/// <param name="PageSize">The requested page size.</param>
/// <param name="AnalysisBatchId">The optional persisted analysis batch identifier.</param>
/// <param name="Decision">The optional exact persisted member-decision filter.</param>
/// <param name="Sort">The allowed sort field, if specified.</param>
public sealed record FontGroupQuery(int Page, int PageSize, long? AnalysisBatchId, string? Decision, string? Sort);
