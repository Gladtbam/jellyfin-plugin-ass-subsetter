using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

/// <summary>Builds deterministic fingerprints without generated identifiers or analysis scores.</summary>
public static class FontInventoryInputComparer
{
    /// <summary>Captures canonical inventory, policy and evidence inputs.</summary>
    /// <param name="files">Completely observed physical files.</param>
    /// <param name="policy">The configuration captured for this scan.</param>
    /// <param name="evidenceKey">The current analysis compatibility key.</param>
    /// <returns>Canonical fingerprints.</returns>
    public static FontAnalysisInputSnapshot Capture(IReadOnlyList<FontFileRecord> files, FontDirectoryPolicyResolver policy, string evidenceKey)
    {
        var stable = files.OrderBy(file => file.NormalizedPath, StringComparer.Ordinal).Select(file => file with
        {
            Id = 0,
            GenerationId = 0,
            Error = null,
            PathAliases = file.PathAliases.OrderBy(path => path, FontPathComparison.Comparer).ToArray(),
            Faces = file.Faces.OrderBy(face => face.FaceIndex).Select(face => face with { Id = 0, RetentionScore = 0, IsRuntimePreferred = false }).ToArray()
        });
        return new(Hash(JsonSerializer.Serialize(stable)), Hash(JsonSerializer.Serialize(policy.GetConfiguredRoots())), Hash(evidenceKey));
    }

    /// <summary>Explains the changed inputs.</summary>
    /// <param name="previous">The published input, if present.</param>
    /// <param name="current">The current observation.</param>
    /// <returns>Stable reason codes.</returns>
    public static IReadOnlyList<string> GetChangeReasons(FontAnalysisInputSnapshot? previous, FontAnalysisInputSnapshot current)
    {
        if (previous is null)
        {
            return ["initial-scan"];
        }

        var reasons = new List<string>();
        if (previous.InventoryFingerprint != current.InventoryFingerprint)
        {
            reasons.Add("files-changed");
        }

        if (previous.PolicyFingerprint != current.PolicyFingerprint)
        {
            reasons.Add("directory-policy-changed");
        }

        if (previous.EvidenceFingerprint != current.EvidenceFingerprint)
        {
            reasons.Add("evidence-version-changed");
        }

        return reasons;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
