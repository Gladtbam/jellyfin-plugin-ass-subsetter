using System.Collections.Generic;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Api;

/// <summary>Selected current candidates without client-supplied paths.</summary>
/// <param name="GenerationId">Displayed publication.</param>
/// <param name="AnalysisBatchId">Displayed analysis.</param>
/// <param name="RuleFingerprint">Displayed naming rules.</param>
/// <param name="SelectedFileIds">Selected physical file identifiers.</param>
/// <param name="IdempotencyKey">Stable retry identity.</param>
public sealed record QueueRenameSelectionRequest(long GenerationId, long AnalysisBatchId, string RuleFingerprint, IReadOnlyList<long> SelectedFileIds, string IdempotencyKey);
