using BenchmarkDotNet.Attributes;
using Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

namespace Jellyfin.Plugin.AssSubsetter.Benchmarks;

/// <summary>Measures exact retention search for directly equivalent two-Face collections.</summary>
[MemoryDiagnoser]
public class RetentionSearchBenchmarks
{
    private readonly FontRetentionSelector _selector = new();
    private DirectoryPolicySnapshot _snapshot = null!;
    private FontRelationRecord[] _relations = null!;

    /// <summary>Gets or sets the component size, including the exact-search upper limit.</summary>
    [Params(12, 18)]
    public int FileCount { get; set; }

    /// <summary>Creates equally scored collections with direct replacement proof for every Face.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var quality = new FontRetentionQualityEvidence(Enum.GetValues<FontQualitySignal>().ToHashSet(), 2m);
        FontRetentionFile[] files = Enumerable.Range(1, FileCount).Select(index => new FontRetentionFile
        {
            FileId = index,
            Path = $"copy-{index:D2}.ttc",
            NormalizedPath = $"COPY-{index:D2}.TTC",
            Size = 100,
            ModifiedUtc = DateTimeOffset.UnixEpoch,
            Health = FontFileHealth.Healthy,
            Faces =
            [
                new FontRetentionFace((index * 2) - 1, "TrueType", true, quality),
                new FontRetentionFace(index * 2, "TrueType", true, quality)
            ]
        }).ToArray();
        var relations = new List<FontRelationRecord>();
        for (int left = 0; left < files.Length; left++)
        {
            for (int right = left + 1; right < files.Length; right++)
            {
                for (int face = 0; face < 2; face++)
                {
                    relations.Add(new FontRelationRecord
                    {
                        LeftFaceId = files[left].Faces[face].FaceId,
                        RightFaceId = files[right].Faces[face].FaceId,
                        RelationshipKind = FontRelationshipKind.StrictFaceDuplicate,
                        ReplacementDirection = FontReplacementDirection.Both,
                        EvidenceVersion = 1,
                        EvidenceJson = "{}"
                    });
                }
            }
        }

        _snapshot = new DirectoryPolicySnapshot(files);
        _relations = relations.ToArray();
    }

    /// <summary>Chooses the minimum retained set using the production search and safety rules.</summary>
    /// <returns>The complete retention plan.</returns>
    [Benchmark]
    public RetentionPlan SelectCollections() => _selector.Select(_relations, _snapshot);
}
