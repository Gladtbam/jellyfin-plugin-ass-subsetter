using System.Collections.Generic;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

/// <summary>Current read-only filename candidates and their authorization context.</summary>
/// <param name="GenerationId">The published generation.</param>
/// <param name="AnalysisBatchId">The completed analysis.</param>
/// <param name="Rules">Current naming rules.</param>
/// <param name="RuleFingerprint">The exact rule digest.</param>
/// <param name="Items">Candidates and safety exclusions.</param>
public sealed record FontRenameSnapshot(long GenerationId, long AnalysisBatchId, FontNamingRules Rules, string RuleFingerprint, IReadOnlyList<FontRenameCandidate> Items);
