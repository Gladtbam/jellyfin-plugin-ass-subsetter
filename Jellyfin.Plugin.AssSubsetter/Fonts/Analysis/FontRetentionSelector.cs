using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;

/// <summary>Selects retained physical files after hard constraints, fixed scoring, and deterministic optimization.</summary>
public sealed class FontRetentionSelector
{
    private static readonly Dictionary<FontQualitySignal, int> ContentWeights = new()
    {
        [FontQualitySignal.CommonGlyphContentCorrect] = 10,
        [FontQualitySignal.CoverageComplete] = 8,
        [FontQualitySignal.ValidCmap] = 5,
        [FontQualitySignal.LayoutComplete] = 5,
        [FontQualitySignal.VerticalMetricsComplete] = 3,
        [FontQualitySignal.VariationSelectorsComplete] = 3,
        [FontQualitySignal.ColorGlyphsComplete] = 3,
        [FontQualitySignal.VariationAxesComplete] = 3
    };

    private static readonly Dictionary<FontQualitySignal, int> VersionWeights = new()
    {
        [FontQualitySignal.ReliableVersion] = 10,
        [FontQualitySignal.ConsistentVersionSources] = 5
    };

    private static readonly Dictionary<FontQualitySignal, int> TechnicalWeights = new()
    {
        [FontQualitySignal.ValidChecksums] = 4,
        [FontQualitySignal.ValidTables] = 5,
        [FontQualitySignal.StableNativeLoaders] = 4,
        [FontQualitySignal.CompatibleTechnology] = 2,
        [FontQualitySignal.EmbeddingRights] = 3,
        [FontQualitySignal.CoherentVendor] = 2
    };

    private static readonly Dictionary<FontQualitySignal, int> NameWeights = new()
    {
        [FontQualitySignal.PostScriptName] = 2,
        [FontQualitySignal.TypographicNames] = 2,
        [FontQualitySignal.WwsNames] = 1,
        [FontQualitySignal.LegacyNames] = 1,
        [FontQualitySignal.FullName] = 1,
        [FontQualitySignal.UniqueId] = 1,
        [FontQualitySignal.AliasesComplete] = 2
    };

    private static readonly HashSet<FontRelationshipKind> AutomaticKinds =
    [
        FontRelationshipKind.IdenticalFileHash,
        FontRelationshipKind.StrictFaceDuplicate,
        FontRelationshipKind.RenderingAndLayoutEquivalent,
        FontRelationshipKind.MetadataOnlyDifference,
        FontRelationshipKind.CoverageSuperset
    ];

    /// <summary>Selects file-level retention and quarantine candidates without inferring replacement transitively.</summary>
    /// <param name="relations">Terminal direct Face relationships from one immutable analysis batch.</param>
    /// <param name="policySnapshot">The physical-file safety and quality snapshot for the same generation.</param>
    /// <returns>A deterministic file-level retention plan and its direct groups.</returns>
    public RetentionPlan Select(IReadOnlyList<FontRelationRecord> relations, DirectoryPolicySnapshot policySnapshot)
    {
        ArgumentNullException.ThrowIfNull(relations);
        ArgumentNullException.ThrowIfNull(policySnapshot);
        Dictionary<long, FontRetentionFile> files = ValidateSnapshot(policySnapshot);
        Dictionary<long, FontRetentionFile> filesByFace = files.Values
            .SelectMany(file => file.Faces.Select(face => new KeyValuePair<long, FontRetentionFile>(face.FaceId, file)))
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        Dictionary<(long Left, long Right), FontRelationRecord> relationsByPair = relations
            .ToDictionary(relation => (relation.LeftFaceId, relation.RightFaceId));

        decimal? newestVersion = files.Values
            .SelectMany(file => file.Faces)
            .Select(face => face.Quality.Version)
            .Where(version => version.HasValue)
            .Max();
        DateTimeOffset newestModified = files.Count == 0
            ? DateTimeOffset.MinValue
            : files.Values.Max(file => file.ModifiedUtc);
        Dictionary<long, RetentionScore> scores = files.Values.ToDictionary(
            file => file.FileId,
            file => ScoreFile(file, newestVersion, newestModified));
        Dictionary<long, HashSet<long>> automaticNeighbors = BuildAutomaticNeighbors(files, filesByFace, relations);

        Dictionary<long, (long Bytes, int Count)> potential = files.Values.ToDictionary(
            file => file.FileId,
            file => PotentialSavings(file, automaticNeighbors[file.FileId].Select(fileId => files[fileId]), scores, relationsByPair));
        FontRetentionFile[] ordered = files.Values
            .OrderByDescending(file => scores[file.FileId].Total)
            .ThenByDescending(file => file.DirectoryPolicy.HasFlag(FontDirectoryPolicy.Preferred))
            .ThenByDescending(file => potential[file.FileId].Bytes)
            .ThenByDescending(file => potential[file.FileId].Count)
            .ThenBy(file => file.NormalizedPath, StringComparer.Ordinal)
            .ToArray();

        var retained = files.Values.Where(file => !IsSafeToQuarantine(file)).Select(file => file.FileId).ToHashSet();
        var quarantined = new HashSet<long>();
        var handled = new HashSet<long>();
        var groups = new List<DuplicateGroupPlan>();
        var replacementReasons = new Dictionary<long, long>();
        foreach (FontRetentionFile[] component in BuildAutomaticComponents(files, automaticNeighbors))
        {
            if (!component.Any(file => file.Faces.Count > 1))
            {
                continue;
            }

            handled.UnionWith(component.Select(file => file.FileId));
            long[]? componentRetained = OptimizeComponent(component, scores, relationsByPair);
            if (componentRetained is null)
            {
                retained.UnionWith(component.Select(file => file.FileId));
                continue;
            }

            HashSet<long> componentRetainedSet = componentRetained.ToHashSet();
            HashSet<long> componentQuarantined = component.Select(file => file.FileId).Where(fileId => !componentRetainedSet.Contains(fileId)).ToHashSet();
            retained.UnionWith(componentRetainedSet);
            quarantined.UnionWith(componentQuarantined);
            foreach (FontRetentionFile target in component.Where(file => componentQuarantined.Contains(file.FileId)))
            {
                long[] combination = FindCombination(target, component, scores, relationsByPair, componentRetainedSet, componentQuarantined)
                    ?? throw new InvalidOperationException("An optimized removal lost its direct retained Face proof.");
                replacementReasons[target.FileId] = combination[0];
                groups.Add(new DuplicateGroupPlan(
                    combination[0],
                    files[combination[0]].Faces[0].FaceId,
                    [target.FileId],
                    target.Size)
                {
                    RetainedFileIds = combination
                });
            }
        }

        foreach (FontRetentionFile candidate in ordered)
        {
            if (handled.Contains(candidate.FileId) || quarantined.Contains(candidate.FileId))
            {
                continue;
            }

            retained.Add(candidate.FileId);
            FontRetentionFile[] removals = automaticNeighbors[candidate.FileId]
                .Select(fileId => files[fileId])
                .Where(other => other.FileId != candidate.FileId
                    && !handled.Contains(other.FileId)
                    && !retained.Contains(other.FileId)
                    && !quarantined.Contains(other.FileId)
                    && scores[candidate.FileId].Total >= scores[other.FileId].Total
                    && IsSafeToQuarantine(other)
                    && CanReplaceFile(candidate, other, relationsByPair))
                .OrderByDescending(other => other.Size)
                .ThenBy(other => other.NormalizedPath, StringComparer.Ordinal)
                .ToArray();
            if (removals.Length == 0)
            {
                continue;
            }

            foreach (FontRetentionFile removal in removals)
            {
                quarantined.Add(removal.FileId);
                replacementReasons[removal.FileId] = candidate.FileId;
            }

            groups.Add(new DuplicateGroupPlan(
                candidate.FileId,
                candidate.Faces[0].FaceId,
                removals.Select(file => file.FileId).ToArray(),
                removals.Sum(file => file.Size)));
        }

        foreach (long fileId in files.Keys)
        {
            if (!quarantined.Contains(fileId))
            {
                retained.Add(fileId);
            }
        }

        if (retained.Overlaps(quarantined)
            || groups.Any(group => group.RetainedFileIds.Any(fileId => !retained.Contains(fileId) || quarantined.Contains(fileId))))
        {
            throw new InvalidOperationException("A quarantine candidate cannot also be a retained replacement.");
        }

        HashSet<long> runtimePreferred = SelectRuntimePreferred(retained, files, filesByFace, relations, scores, potential);
        RetentionFileDecision[] retainedDecisions = retained
            .Select(fileId => Decision(
                files[fileId],
                scores[fileId],
                HardConstraintReason(files[fileId]) ?? "Selected by quality and deterministic tie-breaks.",
                runtimePreferred.Contains(fileId)))
            .OrderBy(decision => decision.FileId)
            .ToArray();
        RetentionFileDecision[] quarantineDecisions = quarantined
            .Select(fileId => Decision(
                files[fileId],
                scores[fileId],
                $"Directly replaced by retained file {replacementReasons[fileId]}.",
                false))
            .OrderBy(decision => decision.FileId)
            .ToArray();
        return new RetentionPlan(retainedDecisions, quarantineDecisions, groups);
    }

    private static Dictionary<long, HashSet<long>> BuildAutomaticNeighbors(
        Dictionary<long, FontRetentionFile> files,
        Dictionary<long, FontRetentionFile> filesByFace,
        IReadOnlyList<FontRelationRecord> relations)
    {
        Dictionary<long, HashSet<long>> neighbors = files.Keys.ToDictionary(fileId => fileId, _ => new HashSet<long>());
        foreach (FontRelationRecord relation in relations)
        {
            if (!AutomaticKinds.Contains(relation.RelationshipKind)
                || !filesByFace.TryGetValue(relation.LeftFaceId, out FontRetentionFile? left)
                || !filesByFace.TryGetValue(relation.RightFaceId, out FontRetentionFile? right)
                || left.FileId == right.FileId)
            {
                continue;
            }

            neighbors[left.FileId].Add(right.FileId);
            neighbors[right.FileId].Add(left.FileId);
        }

        return neighbors;
    }

    private static List<FontRetentionFile[]> BuildAutomaticComponents(
        Dictionary<long, FontRetentionFile> files,
        Dictionary<long, HashSet<long>> neighbors)
    {
        var visited = new HashSet<long>();
        var components = new List<FontRetentionFile[]>();
        foreach (long start in files.Keys.Order())
        {
            if (!visited.Add(start))
            {
                continue;
            }

            var component = new List<FontRetentionFile>();
            var pending = new Queue<long>();
            pending.Enqueue(start);
            while (pending.Count > 0)
            {
                long fileId = pending.Dequeue();
                component.Add(files[fileId]);
                foreach (long neighbor in neighbors[fileId])
                {
                    if (visited.Add(neighbor))
                    {
                        pending.Enqueue(neighbor);
                    }
                }
            }

            components.Add(component.OrderBy(file => file.NormalizedPath, StringComparer.Ordinal).ToArray());
        }

        return components;
    }

    private static long[]? OptimizeComponent(
        FontRetentionFile[] component,
        Dictionary<long, RetentionScore> scores,
        IReadOnlyDictionary<(long Left, long Right), FontRelationRecord> relations)
    {
        // Exhaustively optimize small direct-proof components; a larger graph stays report-only.
        if (component.Length > 18 || component.Sum(file => file.Faces.Count) > 128)
        {
            return null;
        }

        int mandatory = 0;
        var faceReplacements = new int[component.Length][];
        for (int targetIndex = 0; targetIndex < component.Length; targetIndex++)
        {
            FontRetentionFile target = component[targetIndex];
            if (!IsSafeToQuarantine(target) || target.Faces.Count > 63)
            {
                mandatory |= 1 << targetIndex;
            }

            faceReplacements[targetIndex] = new int[target.Faces.Count];
            for (int faceIndex = 0; faceIndex < target.Faces.Count; faceIndex++)
            {
                FontRetentionFace targetFace = target.Faces[faceIndex];
                int[] eligible = Enumerable.Range(0, component.Length)
                    .Where(candidateIndex => candidateIndex != targetIndex
                        && scores[component[candidateIndex].FileId].Total >= scores[target.FileId].Total
                        && (scores[component[candidateIndex].FileId].Total > scores[target.FileId].Total
                            || !target.DirectoryPolicy.HasFlag(FontDirectoryPolicy.Preferred)
                            || component[candidateIndex].DirectoryPolicy.HasFlag(FontDirectoryPolicy.Preferred))
                        && component[candidateIndex].Faces.Any(candidateFace =>
                            string.Equals(candidateFace.Technology, targetFace.Technology, StringComparison.Ordinal)
                            && CanReplaceFace(candidateFace.FaceId, targetFace.FaceId, relations)))
                    .ToArray();
                if (eligible.Length == 0)
                {
                    mandatory |= 1 << targetIndex;
                    continue;
                }

                int bestScore = eligible.Max(index => scores[component[index].FileId].Total);
                bool preferred = eligible.Any(index => scores[component[index].FileId].Total == bestScore
                    && component[index].DirectoryPolicy.HasFlag(FontDirectoryPolicy.Preferred));
                foreach (int candidateIndex in eligible.Where(index => scores[component[index].FileId].Total == bestScore
                    && (!preferred || component[index].DirectoryPolicy.HasFlag(FontDirectoryPolicy.Preferred))))
                {
                    faceReplacements[targetIndex][faceIndex] |= 1 << candidateIndex;
                }
            }
        }

        int bestRetained = (1 << component.Length) - 1;
        long bestReclaimed = -1;
        int bestCount = int.MaxValue;
        for (int retainedMask = 0; retainedMask < 1 << component.Length; retainedMask++)
        {
            if ((retainedMask & mandatory) != mandatory)
            {
                continue;
            }

            long reclaimed = 0;
            bool valid = true;
            for (int targetIndex = 0; targetIndex < component.Length; targetIndex++)
            {
                if ((retainedMask & (1 << targetIndex)) != 0)
                {
                    continue;
                }

                foreach (int candidateMask in faceReplacements[targetIndex])
                {
                    if ((candidateMask & retainedMask) == 0)
                    {
                        valid = false;
                        break;
                    }
                }

                if (!valid)
                {
                    break;
                }

                reclaimed += component[targetIndex].Size;
            }

            if (!valid)
            {
                continue;
            }

            int count = BitOperations.PopCount((uint)retainedMask);
            if (reclaimed > bestReclaimed
                || (reclaimed == bestReclaimed && (count < bestCount
                    || (count == bestCount && PreferEarlierPaths(retainedMask, bestRetained, component.Length)))))
            {
                bestRetained = retainedMask;
                bestReclaimed = reclaimed;
                bestCount = count;
            }
        }

        return Enumerable.Range(0, component.Length)
            .Where(index => (bestRetained & (1 << index)) != 0)
            .Select(index => component[index].FileId)
            .ToArray();
    }

    private static bool PreferEarlierPaths(int candidate, int current, int fileCount)
    {
        for (int index = 0; index < fileCount; index++)
        {
            int bit = 1 << index;
            if ((candidate & bit) != (current & bit))
            {
                return (candidate & bit) != 0;
            }
        }

        return false;
    }

    private static long[]? FindCombination(
        FontRetentionFile target,
        IReadOnlyList<FontRetentionFile> files,
        Dictionary<long, RetentionScore> scores,
        IReadOnlyDictionary<(long Left, long Right), FontRelationRecord> relations,
        HashSet<long> retained,
        HashSet<long> quarantined)
    {
        // Exact search is intentionally bounded. Oversized proof graphs remain report-only.
        if (target.Faces.Count > 63)
        {
            return null;
        }

        var candidates = new Dictionary<long, ulong>();
        for (int faceIndex = 0; faceIndex < target.Faces.Count; faceIndex++)
        {
            FontRetentionFace targetFace = target.Faces[faceIndex];
            FontRetentionFile[] eligible = files.Where(file => file.FileId != target.FileId
                    && !quarantined.Contains(file.FileId)
                    && scores[file.FileId].Total >= scores[target.FileId].Total
                    && file.Faces.Any(face => string.Equals(face.Technology, targetFace.Technology, StringComparison.Ordinal)
                        && CanReplaceFace(face.FaceId, targetFace.FaceId, relations)))
                .ToArray();
            if (eligible.Length == 0)
            {
                return null;
            }

            int quality = eligible.Max(file => scores[file.FileId].Total);
            eligible = eligible.Where(file => scores[file.FileId].Total == quality).ToArray();
            bool hasPreferred = eligible.Any(file => file.DirectoryPolicy.HasFlag(FontDirectoryPolicy.Preferred));
            foreach (FontRetentionFile file in eligible.Where(file => !hasPreferred || file.DirectoryPolicy.HasFlag(FontDirectoryPolicy.Preferred)))
            {
                candidates[file.FileId] = candidates.GetValueOrDefault(file.FileId) | (1UL << faceIndex);
            }
        }

        if (candidates.Count > 32)
        {
            return null;
        }

        Dictionary<long, FontRetentionFile> filesById = files.ToDictionary(file => file.FileId);
        (long FileId, long Size, string Path, ulong Coverage)[] options = candidates
            .Select(pair => (FileId: pair.Key, Size: retained.Contains(pair.Key) ? 0 : filesById[pair.Key].Size, Path: filesById[pair.Key].NormalizedPath, Coverage: pair.Value))
            .OrderBy(option => option.Path, StringComparer.Ordinal)
            .ToArray();
        ulong allFaces = (1UL << target.Faces.Count) - 1;
        long bestBytes = target.Size;
        int[]? best = null;
        var selected = new List<int>();
        int visited = 0;
        bool exhausted = false;

        void Search(ulong covered, long bytes)
        {
            if (++visited > 1_000_000)
            {
                exhausted = true;
                return;
            }

            if (covered == allFaces)
            {
                int[] current = selected.Order().ToArray();
                if (bytes < bestBytes
                    || (bytes == bestBytes && (best is null || current.Length < best.Length
                        || (current.Length == best.Length && ComparePaths(current, best) < 0))))
                {
                    bestBytes = bytes;
                    best = current;
                }

                return;
            }

            ulong uncovered = allFaces & ~covered;
            int face = -1;
            int fewest = int.MaxValue;
            for (int index = 0; index < target.Faces.Count; index++)
            {
                if ((uncovered & (1UL << index)) == 0)
                {
                    continue;
                }

                int count = options.Count(option => (option.Coverage & (1UL << index)) != 0);
                if (count < fewest)
                {
                    fewest = count;
                    face = index;
                }
            }

            foreach (int index in Enumerable.Range(0, options.Length)
                .Where(index => (options[index].Coverage & (1UL << face)) != 0)
                .OrderBy(index => options[index].Size)
                .ThenBy(index => options[index].Path, StringComparer.Ordinal))
            {
                if (options[index].Size > bestBytes - bytes)
                {
                    continue;
                }

                selected.Add(index);
                Search(covered | options[index].Coverage, bytes + options[index].Size);
                selected.RemoveAt(selected.Count - 1);
                if (exhausted)
                {
                    return;
                }
            }
        }

        int ComparePaths(IReadOnlyList<int> left, IReadOnlyList<int> right)
        {
            for (int index = 0; index < left.Count; index++)
            {
                int comparison = StringComparer.Ordinal.Compare(options[left[index]].Path, options[right[index]].Path);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return 0;
        }

        Search(0, 0);
        return exhausted || best is null ? null : best.Select(index => options[index].FileId).ToArray();
    }

    private static Dictionary<long, FontRetentionFile> ValidateSnapshot(DirectoryPolicySnapshot snapshot)
    {
        var files = new Dictionary<long, FontRetentionFile>();
        var faceIds = new HashSet<long>();
        foreach (FontRetentionFile file in snapshot.Files)
        {
            if (file.FileId <= 0 || !files.TryAdd(file.FileId, file) || file.Faces.Count == 0)
            {
                throw new ArgumentException("Retention files must have unique positive identifiers and at least one Face.", nameof(snapshot));
            }

            if (file.Faces.Any(face => face.FaceId <= 0 || !faceIds.Add(face.FaceId)))
            {
                throw new ArgumentException("Retention Faces must have unique positive identifiers.", nameof(snapshot));
            }
        }

        return files;
    }

    private static RetentionScore ScoreFile(FontRetentionFile file, decimal? newestVersion, DateTimeOffset newestModified)
    {
        RetentionScore[] faceScores = file.Faces.Select(face => ScoreFace(face, newestVersion)).ToArray();
        var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (RetentionScore score in faceScores)
        {
            foreach (KeyValuePair<string, int> reason in score.Subreasons)
            {
                reasons[reason.Key] = reasons.GetValueOrDefault(reason.Key) + reason.Value;
            }
        }

        foreach (string key in reasons.Keys.ToArray())
        {
            reasons[key] /= faceScores.Length;
        }

        int modified = file.ModifiedUtc == newestModified ? 5 : 0;
        if (modified > 0)
        {
            reasons["Modified.Newest"] = modified;
        }

        return new RetentionScore(
            faceScores.Sum(score => score.Content) / faceScores.Length,
            faceScores.Sum(score => score.Version) / faceScores.Length,
            faceScores.Sum(score => score.Technical) / faceScores.Length,
            faceScores.Sum(score => score.Names) / faceScores.Length,
            modified,
            reasons);
    }

    internal static IReadOnlyDictionary<long, RetentionScore> ScoreFaces(DirectoryPolicySnapshot snapshot)
    {
        decimal? newestVersion = snapshot.Files.SelectMany(file => file.Faces).Select(face => face.Quality.Version).Where(version => version.HasValue).Max();
        DateTimeOffset newestModified = snapshot.Files.Count == 0 ? DateTimeOffset.MinValue : snapshot.Files.Max(file => file.ModifiedUtc);
        var scores = new Dictionary<long, RetentionScore>();
        foreach (FontRetentionFile file in snapshot.Files)
        {
            foreach (FontRetentionFace face in file.Faces)
            {
                RetentionScore score = ScoreFace(face, newestVersion);
                var reasons = new Dictionary<string, int>(score.Subreasons, StringComparer.Ordinal);
                int modified = file.ModifiedUtc == newestModified ? 5 : 0;
                if (modified > 0)
                {
                    reasons["Modified.Newest"] = modified;
                }

                scores.Add(face.FaceId, score with { Modified = modified, Subreasons = reasons });
            }
        }

        return scores;
    }

    private static RetentionScore ScoreFace(FontRetentionFace face, decimal? newestVersion)
    {
        var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
        int content = AddSignals(face.Quality.Signals, ContentWeights, "Content", reasons);
        int version = AddSignals(face.Quality.Signals, VersionWeights, "Version", reasons);
        if (face.Quality.Version.HasValue && face.Quality.Version == newestVersion)
        {
            version += 10;
            reasons["Version.Newest"] = 10;
        }

        int technical = AddSignals(face.Quality.Signals, TechnicalWeights, "Technical", reasons);
        int names = AddSignals(face.Quality.Signals, NameWeights, "Names", reasons);
        return new RetentionScore(content, version, technical, names, 0, reasons);
    }

    private static int AddSignals(
        IReadOnlySet<FontQualitySignal> signals,
        Dictionary<FontQualitySignal, int> weights,
        string category,
        Dictionary<string, int> reasons)
    {
        int total = 0;
        foreach (KeyValuePair<FontQualitySignal, int> weight in weights)
        {
            if (signals.Contains(weight.Key))
            {
                total += weight.Value;
                reasons[category + '.' + weight.Key] = weight.Value;
            }
        }

        return total;
    }

    private static (long Bytes, int Count) PotentialSavings(
        FontRetentionFile candidate,
        IEnumerable<FontRetentionFile> files,
        Dictionary<long, RetentionScore> scores,
        IReadOnlyDictionary<(long Left, long Right), FontRelationRecord> relations)
    {
        FontRetentionFile[] removable = files.Where(other => other.FileId != candidate.FileId
            && scores[candidate.FileId].Total >= scores[other.FileId].Total
            && IsSafeToQuarantine(other)
            && CanReplaceFile(candidate, other, relations)).ToArray();
        return (removable.Sum(file => file.Size), removable.Length);
    }

    private static bool IsSafeToQuarantine(FontRetentionFile file)
    {
        return !file.DirectoryPolicy.HasFlag(FontDirectoryPolicy.Protected)
            && file.LinkKind == FontLinkKind.None
            && file.Health == FontFileHealth.Healthy
            && !file.IsInUseWithoutLease
            && file.Faces.All(face => face.IsClassifiable);
    }

    private static string? HardConstraintReason(FontRetentionFile file)
    {
        if (file.DirectoryPolicy.HasFlag(FontDirectoryPolicy.Protected))
        {
            return "Retained because its directory is protected.";
        }

        if (file.LinkKind != FontLinkKind.None)
        {
            return "Retained because linked paths are not cleanup candidates.";
        }

        if (file.Health != FontFileHealth.Healthy || file.Faces.Any(face => !face.IsClassifiable))
        {
            return "Retained because complete classification evidence is unavailable.";
        }

        return file.IsInUseWithoutLease ? "Retained because no maintenance lease is available." : null;
    }

    private static bool CanReplaceFile(
        FontRetentionFile candidate,
        FontRetentionFile target,
        IReadOnlyDictionary<(long Left, long Right), FontRelationRecord> relations)
    {
        return target.Faces.All(targetFace => candidate.Faces.Any(candidateFace =>
            string.Equals(candidateFace.Technology, targetFace.Technology, StringComparison.Ordinal)
            && CanReplaceFace(candidateFace.FaceId, targetFace.FaceId, relations)));
    }

    private static bool CanReplaceFace(
        long candidateFaceId,
        long targetFaceId,
        IReadOnlyDictionary<(long Left, long Right), FontRelationRecord> relations)
    {
        long left = Math.Min(candidateFaceId, targetFaceId);
        long right = Math.Max(candidateFaceId, targetFaceId);
        if (!relations.TryGetValue((left, right), out FontRelationRecord? relation)
            || !AutomaticKinds.Contains(relation.RelationshipKind))
        {
            return false;
        }

        return relation.ReplacementDirection == FontReplacementDirection.Both
            || (candidateFaceId == left && relation.ReplacementDirection == FontReplacementDirection.LeftReplacesRight)
            || (candidateFaceId == right && relation.ReplacementDirection == FontReplacementDirection.RightReplacesLeft);
    }

    private static HashSet<long> SelectRuntimePreferred(
        HashSet<long> retained,
        Dictionary<long, FontRetentionFile> files,
        Dictionary<long, FontRetentionFile> filesByFace,
        IReadOnlyList<FontRelationRecord> relations,
        Dictionary<long, RetentionScore> scores,
        Dictionary<long, (long Bytes, int Count)> potential)
    {
        var adjacency = retained.ToDictionary(fileId => fileId, _ => new HashSet<long>());
        foreach (FontRelationRecord relation in relations)
        {
            if (!filesByFace.TryGetValue(relation.LeftFaceId, out FontRetentionFile? left)
                || !filesByFace.TryGetValue(relation.RightFaceId, out FontRetentionFile? right)
                || left.FileId == right.FileId
                || !retained.Contains(left.FileId)
                || !retained.Contains(right.FileId))
            {
                continue;
            }

            adjacency[left.FileId].Add(right.FileId);
            adjacency[right.FileId].Add(left.FileId);
        }

        var preferred = new HashSet<long>();
        var visited = new HashSet<long>();
        foreach (long start in retained.Order())
        {
            if (!visited.Add(start))
            {
                continue;
            }

            var component = new List<long>();
            var queue = new Queue<long>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                long current = queue.Dequeue();
                component.Add(current);
                foreach (long neighbor in adjacency[current])
                {
                    if (visited.Add(neighbor))
                    {
                        queue.Enqueue(neighbor);
                    }
                }
            }

            long selected = component
                .OrderByDescending(fileId => scores[fileId].Total)
                .ThenByDescending(fileId => files[fileId].DirectoryPolicy.HasFlag(FontDirectoryPolicy.Preferred))
                .ThenByDescending(fileId => potential[fileId].Bytes)
                .ThenByDescending(fileId => potential[fileId].Count)
                .ThenBy(fileId => files[fileId].NormalizedPath, StringComparer.Ordinal)
                .First();
            preferred.Add(selected);
        }

        return preferred;
    }

    private static RetentionFileDecision Decision(
        FontRetentionFile file,
        RetentionScore score,
        string reason,
        bool runtimePreferred)
        => new(file.FileId, file.Name, file.Path, score, reason, runtimePreferred);
}
