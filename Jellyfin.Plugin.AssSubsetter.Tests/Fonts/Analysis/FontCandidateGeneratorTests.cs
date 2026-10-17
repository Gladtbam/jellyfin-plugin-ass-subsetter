using System.Text;
using Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Analysis;

public sealed class FontCandidateGeneratorTests
{
    [Fact]
    public void Generate_ShouldBoundDeepComparisonsToCandidateBucketsAtTwentyThousandFaces()
    {
        var faces = new List<FontFaceEvidence>(20_002);
        for (int index = 0; index < 20_000; index++)
        {
            faces.Add(Face(index + 1, "UNRELATED-" + index, "strict-" + index));
        }

        faces.Add(Face(30_001, "CONTROLLED-DUPLICATE", "controlled-strict"));
        faces.Add(Face(30_002, "CONTROLLED-DUPLICATE", "controlled-strict"));

        FontCandidateGenerationResult result = FontCandidateGenerator.Generate(faces);

        FontCandidatePair pair = Assert.Single(result.Pairs);
        Assert.Equal(30_001, pair.LeftFaceId);
        Assert.Equal(30_002, pair.RightFaceId);
        Assert.InRange(result.BucketPairCount, 1, 8);
        Assert.Equal(1, result.DeepComparisonCount);
        Assert.True(result.DeepComparisonCount < 20_002L * 20_001L / 2L);
    }

    [Fact]
    public void Generate_ShouldDeduplicateUnorderedPairsAcrossIndependentBuckets()
    {
        FontFaceEvidence left = Face(7, "EXAMPLE", "same-strict");
        FontFaceEvidence right = Face(3, "EXAMPLE", "same-strict");

        FontCandidateGenerationResult result = FontCandidateGenerator.Generate([left, right]);

        FontCandidatePair pair = Assert.Single(result.Pairs);
        Assert.Equal(3, pair.LeftFaceId);
        Assert.Equal(7, pair.RightFaceId);
        Assert.True(result.BucketPairCount > result.DeepComparisonCount);
    }

    [Fact]
    public void Generate_ShouldPreservePairsFromPartiallyOverlappingBuckets()
    {
        FontFaceEvidence[] faces =
        [
            Face(4, "FOUR", "strict-four"),
            Face(3, "THREE", "shared-strict"),
            Face(2, "SHARED", "shared-strict"),
            Face(1, "SHARED", "strict-one")
        ];

        FontCandidateGenerationResult result = FontCandidateGenerator.Generate(faces);

        Assert.Equal([new FontCandidatePair(1, 2), new FontCandidatePair(2, 3)], result.Pairs);
        Assert.Equal(5, result.BucketPairCount);
    }

    [Fact]
    public void Generate_ShouldPreserveBucketWorkCountForIdenticalMemberships()
    {
        FontFaceEvidence template = Face(1, "EXAMPLE", "shared-strict");
        FontFaceEvidence[] faces = [template with { FaceId = 7 }, template with { FaceId = 3 }, template];

        FontCandidateGenerationResult result = FontCandidateGenerator.Generate(faces);

        Assert.Equal([new FontCandidatePair(1, 3), new FontCandidatePair(1, 7), new FontCandidatePair(3, 7)], result.Pairs);
        // Hash, strict fingerprint, identity, PostScript, two names and coverage: seven buckets.
        Assert.Equal(21, result.BucketPairCount);
        Assert.Equal(result.Pairs, FontCandidateGenerator.Generate(faces.Reverse().ToArray()).Pairs);
    }

    private static FontFaceEvidence Face(long faceId, string identity, string strict)
    {
        uint scalar = checked((uint)(0x1000 + faceId));
        return new FontFaceEvidence
        {
            FaceId = faceId,
            FileId = faceId,
            IsClassifiable = true,
            PhysicalFileId = "file-" + faceId,
            FaceIndex = 0,
            FileHash = Bytes("file-hash-" + faceId),
            StrictFingerprint = Bytes(strict),
            SemanticFingerprints = new Dictionary<FontSemanticGroup, ReadOnlyMemory<byte>>
            {
                [FontSemanticGroup.Mapping] = Bytes("mapping-" + identity)
            },
            Identity = new FontIdentityEvidence(
                identity,
                identity,
                "REGULAR",
                16,
                17,
                identity + " REGULAR",
                "VENDOR:" + identity,
                identity,
                new HashSet<string> { identity, identity + " REGULAR" },
                "TEST",
                400,
                5,
                0,
                new FontVersionEvidence(null, false, [])),
            Technology = "TrueType",
            UnitsPerEm = 1000,
            UnicodeCoverage = [new UnicodeRange(scalar, scalar)],
            CharacterFingerprints = new Dictionary<uint, ReadOnlyMemory<byte>>
            {
                [scalar] = Bytes("glyph-" + identity)
            }
        };
    }

    private static ReadOnlyMemory<byte> Bytes(string value) => Encoding.UTF8.GetBytes(value);
}
