using System.Collections.Generic;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>Immutable server-computed authorization for selected physical moves.</summary>
/// <param name="GenerationId">The published generation.</param>
/// <param name="AnalysisBatchId">The completed analysis.</param>
/// <param name="Rules">Current naming rules.</param>
/// <param name="RuleFingerprint">The exact rule digest.</param>
/// <param name="Items">Candidates and safety exclusions.</param>
public sealed record FontRenameExecutionPayload(long GenerationId, long AnalysisBatchId, FontNamingRules Rules, string RuleFingerprint, IReadOnlyList<RenameItemRecord> Items);
