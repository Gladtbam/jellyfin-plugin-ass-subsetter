using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;

/// <summary>Orchestrates bounded direct comparison, retention selection, immutable persistence, and report pruning.</summary>
public sealed class FontDuplicateAnalyzer
{
    internal const int CurrentEvidenceVersion = 3;
    private const int MaxDeepFilesPerBatch = 64;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IFontAnalysisEvidenceSource _evidenceSource;
    private readonly FontRelationshipClassifier _classifier;
    private readonly FontRetentionSelector _retentionSelector;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="FontDuplicateAnalyzer"/> class.</summary>
    /// <param name="evidenceSource">The generation-scoped deep evidence source.</param>
    /// <param name="classifier">The strict first-match pair classifier.</param>
    /// <param name="retentionSelector">The physical-file retention selector.</param>
    /// <param name="timeProvider">The UTC clock used for report timestamps and pruning.</param>
    public FontDuplicateAnalyzer(
        IFontAnalysisEvidenceSource evidenceSource,
        FontRelationshipClassifier classifier,
        FontRetentionSelector retentionSelector,
        TimeProvider timeProvider)
    {
        _evidenceSource = evidenceSource ?? throw new ArgumentNullException(nameof(evidenceSource));
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
        _retentionSelector = retentionSelector ?? throw new ArgumentNullException(nameof(retentionSelector));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>Analyzes one inventory generation and reports deep comparison progress.</summary>
    /// <param name="generationId">The published inventory generation identifier.</param>
    /// <param name="progress">Receives percentage completion from zero through one hundred.</param>
    /// <param name="cancellationToken">A token that cancels before immutable persistence.</param>
    /// <returns>The complete unpersisted analysis batch.</returns>
    public async Task<AnalysisBatch> AnalyzeAsync(long generationId, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generationId);
        progress?.Report(0);
        DateTimeOffset startedUtc = _timeProvider.GetUtcNow();
        FontAnalysisSnapshot snapshot = await _evidenceSource.LoadAsync(generationId, cancellationToken).ConfigureAwait(false);
        Dictionary<long, FontFaceEvidence> faces = snapshot.Faces.ToDictionary(face => face.FaceId);
        FontCandidateGenerationResult candidates = FontCandidateGenerator.Generate(snapshot.Faces);
        progress?.Report(5);
        DirectoryPolicySnapshot policy = snapshot.Policy;
        var relations = new FontRelationRecord[candidates.Pairs.Count];
        if (_evidenceSource is IFontDeepAnalysisEvidenceSource deepSource && candidates.Pairs.Count > 0)
        {
            Dictionary<long, FontRetentionFile> files = policy.Files.ToDictionary(file => file.FileId);
            int completedPairs = 0;
            foreach ((long[] fileIds, int[] pairIndexes) in BuildCandidateBatches(candidates.Pairs, faces))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FontDeepAnalysisEvidence deep = await deepSource.LoadFilesAsync(
                    generationId,
                    fileIds,
                    cancellationToken).ConfigureAwait(false);
                foreach (FontFaceEvidence face in deep.Faces)
                {
                    faces[face.FaceId] = face;
                }

                foreach (FontRetentionFile file in deep.Files)
                {
                    files[file.FileId] = file;
                }

                foreach (int index in pairIndexes)
                {
                    FontCandidatePair pair = candidates.Pairs[index];
                    FontRelationshipResult result = _classifier.Classify(faces[pair.LeftFaceId], faces[pair.RightFaceId]);
                    relations[index] = ToRelation(pair, result, faces[pair.LeftFaceId], faces[pair.RightFaceId]);
                }

                // Retain only the small fields needed for final scores; glyph evidence belongs to this group.
                foreach (FontFaceEvidence face in deep.Faces)
                {
                    faces[face.FaceId] = face with
                    {
                        CharacterFingerprints = new Dictionary<uint, ReadOnlyMemory<byte>>(),
                        VariationFingerprints = new Dictionary<ulong, ReadOnlyMemory<byte>>(),
                        UnicodeCoverage = [],
                        VariationSelectors = new HashSet<uint>()
                    };
                }

                completedPairs += pairIndexes.Length;
                progress?.Report(5 + (completedPairs * 85.0 / candidates.Pairs.Count));
            }

            policy = new DirectoryPolicySnapshot(files.Values.OrderBy(file => file.FileId).ToArray());
        }
        else
        {
            for (int index = 0; index < candidates.Pairs.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FontCandidatePair pair = candidates.Pairs[index];
                FontRelationshipResult result = _classifier.Classify(faces[pair.LeftFaceId], faces[pair.RightFaceId]);
                relations[index] = ToRelation(pair, result, faces[pair.LeftFaceId], faces[pair.RightFaceId]);
                progress?.Report(5 + ((index + 1) * 85d / candidates.Pairs.Count));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(90);
        RetentionPlan retention = _retentionSelector.Select(relations, policy);
        DuplicateGroupRecord[] groups = BuildGroups(retention, policy);
        progress?.Report(95);
        FontFaceAnalysisRecord[] faceScores = BuildFaceScores(faces, policy);
        Dictionary<string, int> classCounts = relations
            .GroupBy(relation => relation.RelationshipKind.ToString())
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        DateTimeOffset completedUtc = _timeProvider.GetUtcNow();
        string summaryJson = JsonSerializer.Serialize(
            new
            {
                evidenceVersion = CurrentEvidenceVersion,
                candidateCount = candidates.BucketPairCount,
                comparisonCount = candidates.DeepComparisonCount,
                classCounts,
                retainedFiles = retention.RetainedFiles,
                quarantineCandidates = retention.QuarantineFiles,
                groups = retention.Groups,
                faceScores,
                reclaimableBytes = retention.ReclaimableBytes
            },
            JsonOptions);
        var batch = new AnalysisBatch
        {
            GenerationId = generationId,
            Status = AnalysisBatchStatus.Complete,
            EvidenceVersion = CurrentEvidenceVersion,
            StartedUtc = startedUtc,
            CompletedUtc = completedUtc,
            CandidateCount = candidates.BucketPairCount,
            ComparisonCount = candidates.DeepComparisonCount,
            ReclaimableBytes = retention.ReclaimableBytes,
            SummaryJson = summaryJson,
            Relations = relations,
            Groups = groups,
            FaceScores = faceScores
        };
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(100);
        return batch;
    }

    private static (long[] FileIds, int[] PairIndexes)[] BuildCandidateBatches(
        IReadOnlyList<FontCandidatePair> pairs,
        Dictionary<long, FontFaceEvidence> faces)
    {
        var parents = new Dictionary<long, long>();
        long Find(long fileId)
        {
            if (!parents.TryGetValue(fileId, out long parent))
            {
                parents.Add(fileId, fileId);
                return fileId;
            }

            if (parent != fileId)
            {
                parents[fileId] = Find(parent);
            }

            return parents[fileId];
        }

        foreach (FontCandidatePair pair in pairs)
        {
            long left = Find(faces[pair.LeftFaceId].FileId);
            long right = Find(faces[pair.RightFaceId].FileId);
            if (left != right)
            {
                parents[Math.Max(left, right)] = Math.Min(left, right);
            }
        }

        var groupedIndexes = new Dictionary<long, List<int>>();
        for (int index = 0; index < pairs.Count; index++)
        {
            long root = Find(faces[pairs[index].LeftFaceId].FileId);
            if (!groupedIndexes.TryGetValue(root, out List<int>? indexes))
            {
                indexes = [];
                groupedIndexes.Add(root, indexes);
            }

            indexes.Add(index);
        }

        (long[] FileIds, int[] PairIndexes)[] components = groupedIndexes.Values
            .OrderBy(indexes => indexes[0])
            .Select(indexes => (
                indexes.SelectMany(index => new[]
                    {
                        faces[pairs[index].LeftFaceId].FileId,
                        faces[pairs[index].RightFaceId].FileId
                    })
                    .Distinct()
                    .Order()
                    .ToArray(),
                indexes.ToArray()))
            .ToArray();
        var batches = new List<(long[] FileIds, int[] PairIndexes)>();
        var batchFiles = new List<long>();
        var batchPairs = new List<int>();
        foreach ((long[] fileIds, int[] pairIndexes) in components)
        {
            if (batchFiles.Count > 0 && batchFiles.Count + fileIds.Length > MaxDeepFilesPerBatch)
            {
                batches.Add((batchFiles.Order().ToArray(), batchPairs.ToArray()));
                batchFiles.Clear();
                batchPairs.Clear();
            }

            batchFiles.AddRange(fileIds);
            batchPairs.AddRange(pairIndexes);
        }

        if (batchFiles.Count > 0)
        {
            batches.Add((batchFiles.Order().ToArray(), batchPairs.ToArray()));
        }

        return batches.ToArray();
    }

    private static FontRelationRecord ToRelation(FontCandidatePair pair, FontRelationshipResult result, FontFaceEvidence left, FontFaceEvidence right)
    {
        FontReplacementDirection direction = (result.LeftCanReplaceRight, result.RightCanReplaceLeft) switch
        {
            (true, true) => FontReplacementDirection.Both,
            (true, false) => FontReplacementDirection.LeftReplacesRight,
            (false, true) => FontReplacementDirection.RightReplacesLeft,
            _ => FontReplacementDirection.None
        };
        string evidenceJson = JsonSerializer.Serialize(
            new
            {
                result.EvidenceKeys,
                result.DifferingCodePoints,
                result.LeftCanReplaceRight,
                result.RightCanReplaceLeft,
                leftDiagnostic = result.Kind == FontRelationshipKind.Unclassifiable ? left.ClassificationError : null,
                rightDiagnostic = result.Kind == FontRelationshipKind.Unclassifiable ? right.ClassificationError : null,
                differenceTypes = result.Kind is FontRelationshipKind.SmallGlyphDifference
                    or FontRelationshipKind.VersionConflict
                    or FontRelationshipKind.SameIdentityContentConflict
                    or FontRelationshipKind.PostScriptNameConflict
                    or FontRelationshipKind.CrossNameConflict
                    ? Enum.GetValues<FontSemanticGroup>()
                        .Where(group => !SameSemanticGroup(left, right, group))
                        .Select(group => group.ToString())
                        .ToArray()
                    : []
            },
            JsonOptions);
        return new FontRelationRecord
        {
            LeftFaceId = pair.LeftFaceId,
            RightFaceId = pair.RightFaceId,
            RelationshipKind = result.Kind,
            ReplacementDirection = direction,
            EvidenceVersion = CurrentEvidenceVersion,
            EvidenceJson = evidenceJson
        };
    }

    private static bool SameSemanticGroup(FontFaceEvidence left, FontFaceEvidence right, FontSemanticGroup group)
    {
        bool leftHas = left.SemanticFingerprints.TryGetValue(group, out ReadOnlyMemory<byte> leftHash);
        bool rightHas = right.SemanticFingerprints.TryGetValue(group, out ReadOnlyMemory<byte> rightHash);
        return leftHas == rightHas && (!leftHas || leftHash.Span.SequenceEqual(rightHash.Span));
    }

    private static FontFaceAnalysisRecord[] BuildFaceScores(IReadOnlyDictionary<long, FontFaceEvidence> faces, DirectoryPolicySnapshot policy)
    {
        IReadOnlyDictionary<long, RetentionScore> scores = FontRetentionSelector.ScoreFaces(policy);
        Dictionary<string, int> best = faces.Values.GroupBy(RuntimeIdentity, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(face => scores[face.FaceId].Total), StringComparer.Ordinal);
        return policy.Files.SelectMany(file => file.Faces.Select(face => new FontFaceAnalysisRecord
        {
            FaceId = face.FaceId,
            FileId = file.FileId,
            RetentionScore = scores[face.FaceId].Total,
            IsRuntimePreferred = faces[face.FaceId].IsDeepEvidenceComplete && scores[face.FaceId].Total == best[RuntimeIdentity(faces[face.FaceId])],
            ScoreJson = JsonSerializer.Serialize(scores[face.FaceId], JsonOptions),
            VerificationJson = JsonSerializer.Serialize(face.Quality.Verification, JsonOptions)
        })).OrderBy(face => face.FaceId).ToArray();
    }

    private static string RuntimeIdentity(FontFaceEvidence face)
        => (face.Identity.PrimaryIdentity ?? face.Identity.FamilyName) + "\u001f" + face.Identity.SubfamilyName;

    private static DuplicateGroupRecord[] BuildGroups(RetentionPlan plan, DirectoryPolicySnapshot snapshot)
    {
        Dictionary<long, FontRetentionFile> files = snapshot.Files.ToDictionary(file => file.FileId);
        Dictionary<long, RetentionFileDecision> decisions = plan.RetainedFiles
            .Concat(plan.QuarantineFiles)
            .ToDictionary(decision => decision.FileId);
        return plan.Groups.Select(group =>
        {
            var members = new List<DuplicateGroupMemberRecord>();
            foreach (long retainedFileId in group.RetainedFileIds)
            {
                FontRetentionFile retained = files[retainedFileId];
                RetentionFileDecision retainedDecision = decisions[retained.FileId];
                members.AddRange(retained.Faces.Select(face => Member(
                    face.FaceId,
                    retained,
                    retainedDecision,
                    DuplicateGroupMemberDecision.Retained)));
            }

            foreach (long quarantineFileId in group.QuarantineFileIds)
            {
                FontRetentionFile quarantine = files[quarantineFileId];
                RetentionFileDecision quarantineDecision = decisions[quarantineFileId];
                members.AddRange(
                    quarantine.Faces.Select(
                        face => Member(
                            face.FaceId,
                            quarantine,
                            quarantineDecision,
                            DuplicateGroupMemberDecision.QuarantineCandidate)));
            }

            return new DuplicateGroupRecord
            {
                RetainedFaceId = group.RetainedFaceId,
                ReclaimableBytes = group.ReclaimableBytes,
                EvidenceJson = JsonSerializer.Serialize(group, JsonOptions),
                Members = members
            };
        }).ToArray();
    }

    private static DuplicateGroupMemberRecord Member(
        long faceId,
        FontRetentionFile file,
        RetentionFileDecision decision,
        DuplicateGroupMemberDecision memberDecision)
    {
        return new DuplicateGroupMemberRecord
        {
            FaceId = faceId,
            FileId = file.FileId,
            Decision = memberDecision,
            RetentionScore = decision.Score.Total,
            ScoreJson = JsonSerializer.Serialize(decision.Score, JsonOptions),
            Reason = decision.Reason
        };
    }
}
