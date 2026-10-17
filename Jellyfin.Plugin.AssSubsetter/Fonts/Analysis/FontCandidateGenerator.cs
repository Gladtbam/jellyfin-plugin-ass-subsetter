using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;

/// <summary>Generates bounded candidate pairs from independent exact evidence buckets.</summary>
public static class FontCandidateGenerator
{
    /// <summary>Builds deterministic unordered candidate pairs without an all-Faces Cartesian comparison.</summary>
    /// <param name="faces">Classifiable Face evidence with unique positive persisted identifiers.</param>
    /// <returns>Bucket work counters and deduplicated Face pairs.</returns>
    public static FontCandidateGenerationResult Generate(IReadOnlyList<FontFaceEvidence> faces)
    {
        ArgumentNullException.ThrowIfNull(faces);
        var buckets = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        var faceIds = new HashSet<long>();
        foreach (FontFaceEvidence face in faces)
        {
            if (face.FaceId <= 0 || !faceIds.Add(face.FaceId))
            {
                throw new ArgumentException("Candidate Faces must have unique positive identifiers.", nameof(faces));
            }

            AddDigestBucket(buckets, "H:" + face.FaceIndex.ToString(CultureInfo.InvariantCulture) + ':', face.FileHash, face.FaceId);
            AddDigestBucket(buckets, "S:", face.StrictFingerprint, face.FaceId);
            AddIdentityBuckets(buckets, face);
            AddCoverageBucket(buckets, face);
        }

        long bucketPairCount = 0;
        var uniquePairs = new HashSet<(long Left, long Right)>();
        var expandedBuckets = new HashSet<List<long>>(BucketMembershipComparer.Instance);
        foreach (List<long> bucket in buckets.Values.Where(values => values.Count > 1))
        {
            bucket.Sort();
            bucketPairCount = checked(bucketPairCount + ((long)bucket.Count * (bucket.Count - 1) / 2));
            // Independent evidence often selects exactly the same Faces. Keep the original
            // work counter, but expand each membership only once. Buckets are now immutable.
            if (!expandedBuckets.Add(bucket))
            {
                continue;
            }

            for (int leftIndex = 0; leftIndex < bucket.Count - 1; leftIndex++)
            {
                for (int rightIndex = leftIndex + 1; rightIndex < bucket.Count; rightIndex++)
                {
                    uniquePairs.Add((bucket[leftIndex], bucket[rightIndex]));
                }
            }
        }

        FontCandidatePair[] pairs = uniquePairs
            .OrderBy(pair => pair.Left)
            .ThenBy(pair => pair.Right)
            .Select(pair => new FontCandidatePair(pair.Left, pair.Right))
            .ToArray();
        return new FontCandidateGenerationResult(pairs, bucketPairCount);
    }

    private static void AddIdentityBuckets(Dictionary<string, List<long>> buckets, FontFaceEvidence face)
    {
        FontIdentityEvidence identity = face.Identity;
        string styleKey = string.Create(
            CultureInfo.InvariantCulture,
            $"|{identity.Weight}|{identity.Width}|{identity.Slant}|{face.Technology}");
        AddValueBucket(buckets, "I:", identity.PrimaryIdentity, styleKey, face.FaceId);
        AddValueBucket(buckets, "P:", identity.PostScriptName, string.Empty, face.FaceId);

        var candidateNames = new HashSet<string>(StringComparer.Ordinal);
        AddName(candidateNames, identity.PostScriptName, identity.SubfamilyName);
        AddName(candidateNames, identity.FamilyName, identity.SubfamilyName);
        AddName(candidateNames, identity.FullName, identity.SubfamilyName);
        foreach (string name in identity.AllNames)
        {
            AddName(candidateNames, name, identity.SubfamilyName);
        }

        foreach (string name in candidateNames)
        {
            AddValueBucket(buckets, "N:", name, string.Empty, face.FaceId);
        }
    }

    private static void AddCoverageBucket(Dictionary<string, List<long>> buckets, FontFaceEvidence face)
    {
        string? identity = face.Identity.FamilyName ?? face.Identity.PrimaryIdentity;
        if (identity is null || face.UnicodeCoverage.Count == 0)
        {
            return;
        }

        var signature = new StringBuilder(identity.Length + (face.UnicodeCoverage.Count * 20));
        signature.Append("C:").Append(identity).Append('|').Append(face.Technology).Append('|');
        foreach (Inventory.UnicodeRange range in face.UnicodeCoverage)
        {
            signature.Append(range.Start.ToString("X", CultureInfo.InvariantCulture))
                .Append('-')
                .Append(range.End.ToString("X", CultureInfo.InvariantCulture))
                .Append(',');
        }

        AddBucket(buckets, signature.ToString(), face.FaceId);
    }

    private static void AddName(HashSet<string> names, string? value, string? subfamily)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && value.Length >= 4
            && !string.Equals(value, subfamily, StringComparison.Ordinal))
        {
            names.Add(value);
        }
    }

    private static void AddDigestBucket(
        Dictionary<string, List<long>> buckets,
        string prefix,
        ReadOnlyMemory<byte> digest,
        long faceId)
    {
        if (digest.Length > 0)
        {
            AddBucket(buckets, prefix + Convert.ToHexString(digest.Span), faceId);
        }
    }

    private static void AddValueBucket(
        Dictionary<string, List<long>> buckets,
        string prefix,
        string? value,
        string suffix,
        long faceId)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            AddBucket(buckets, prefix + value + suffix, faceId);
        }
    }

    private static void AddBucket(Dictionary<string, List<long>> buckets, string key, long faceId)
    {
        if (!buckets.TryGetValue(key, out List<long>? bucket))
        {
            bucket = [];
            buckets.Add(key, bucket);
        }

        bucket.Add(faceId);
    }

    private sealed class BucketMembershipComparer : IEqualityComparer<List<long>>
    {
        public static BucketMembershipComparer Instance { get; } = new();

        public bool Equals(List<long>? left, List<long>? right)
            => ReferenceEquals(left, right)
                || (left is not null && right is not null && left.SequenceEqual(right));

        public int GetHashCode(List<long> bucket)
        {
            HashCode hash = default;
            foreach (long faceId in bucket)
            {
                hash.Add(faceId);
            }

            return hash.ToHashCode();
        }
    }
}
