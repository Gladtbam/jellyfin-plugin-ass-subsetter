using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;

namespace Jellyfin.Plugin.AssSubsetter.Benchmarks;

/// <summary>Measures the production bucket-bounded candidate pipeline over 20,000 generated files.</summary>
[MemoryDiagnoser]
[MedianColumn]
public class CandidateGenerationBenchmarks
{
    private FontFaceEvidence[] _faces = null!;

    /// <summary>Creates controlled buckets outside the timed region.</summary>
    [GlobalSetup]
    public void Setup() => _faces = SyntheticInventory.CreateEvidence();

    /// <summary>Runs production candidate generation instead of an all-pairs comparison.</summary>
    /// <returns>Actual candidate and deep-comparison counts.</returns>
    [Benchmark]
    public FontCandidateGenerationResult GenerateCandidates() => FontCandidateGenerator.Generate(_faces);
}
