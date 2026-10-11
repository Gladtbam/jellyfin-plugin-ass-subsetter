using System.Collections.Generic;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;

/// <summary>Reports bounded candidate-bucket work and deduplicated deep-comparison requests.</summary>
/// <param name="Pairs">The deterministic unordered Face pairs to classify.</param>
/// <param name="BucketPairCount">The sum of pair counts produced by all buckets before deduplication.</param>
public sealed record FontCandidateGenerationResult(
    IReadOnlyList<FontCandidatePair> Pairs,
    long BucketPairCount)
{
    /// <summary>Gets the number of unique deep comparisons required.</summary>
    public long DeepComparisonCount => Pairs.Count;
}
