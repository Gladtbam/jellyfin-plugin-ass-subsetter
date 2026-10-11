using System.Collections.Generic;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

/// <summary>A complete scan, either staged for publication or reusing the current publication.</summary>
/// <param name="GenerationId">The staged or reused generation.</param>
/// <param name="HasChanges">Whether publication is required.</param>
/// <param name="Input">The observed input.</param>
/// <param name="ChangeReasons">Concrete reasons for replacing the published analysis.</param>
public sealed record FontInventoryScanResult(long GenerationId, bool HasChanges, FontAnalysisInputSnapshot Input, IReadOnlyList<string> ChangeReasons);
