using System.Collections.Generic;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Api;

/// <summary>Current authorization context without full candidate data.</summary>
/// <param name="GenerationId">Published generation.</param>
/// <param name="AnalysisBatchId">Current complete analysis.</param>
/// <param name="Rules">Current naming rules.</param>
/// <param name="RuleFingerprint">Exact rule digest.</param>
public sealed record FontRenameContextDto(long GenerationId, long AnalysisBatchId, FontNamingRules Rules, string RuleFingerprint);
