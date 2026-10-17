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
    private FontFaceEvidence[] _duplicateFaces = null!;

    /// <summary>Creates controlled buckets outside the timed region.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _faces = SyntheticInventory.CreateEvidence();
        _duplicateFaces = new FontFaceEvidence[2_000];
        for (int index = 0; index < _duplicateFaces.Length; index++)
        {
            FontFaceEvidence template = _faces[index / 200];
            _duplicateFaces[index] = template with
            {
                FaceId = index + 1,
                FileId = index + 1,
                PhysicalFileId = $"duplicate-file-{index}",
                Identity = template.Identity with
                {
                    AllNames = Enumerable.Range(0, 8)
                        .Select(alias => $"{template.Identity.FamilyName} Alias {alias}")
                        .ToHashSet(StringComparer.Ordinal)
                }
            };
        }
    }

    /// <summary>Runs production candidate generation instead of an all-pairs comparison.</summary>
    /// <returns>Actual candidate and deep-comparison counts.</returns>
    [Benchmark]
    public FontCandidateGenerationResult GenerateCandidates() => FontCandidateGenerator.Generate(_faces);

    /// <summary>Measures repeated copies whose hashes and localized names select the same candidate groups.</summary>
    /// <returns>The deduplicated candidates and original bucket work count.</returns>
    [Benchmark]
    public FontCandidateGenerationResult GenerateDuplicateCandidates() => FontCandidateGenerator.Generate(_duplicateFaces);
}
