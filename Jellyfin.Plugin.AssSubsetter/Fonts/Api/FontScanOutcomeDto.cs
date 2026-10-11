using System.Collections.Generic;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Api;

/// <summary>Describes the successful publication outcome of a complete scan.</summary>
/// <param name="GenerationId">The published or reused generation.</param>
/// <param name="AnalysisBatchId">The published or reused analysis.</param>
/// <param name="Changed">Whether a new publication was committed.</param>
/// <param name="ChangeReasons">The changed analysis inputs.</param>
public sealed record FontScanOutcomeDto(long GenerationId, long AnalysisBatchId, bool Changed, IReadOnlyList<string> ChangeReasons);
