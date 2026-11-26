using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <summary>The metadata used to select a report page and its snapshot-scoped evidence reader.</summary>
internal sealed record FontReportSnapshot(
    long BatchId,
    IReadOnlyList<FontReportSnapshot.FontReportRelation> Relations,
    IReadOnlyList<FontReportSnapshot.FontReportGroup> Groups,
    IReadOnlyList<FontReportSnapshot.FontReportFile> Files,
    Func<long[], long[], CancellationToken, Task<FontReportSnapshot.FontReportEvidence>> LoadEvidenceAsync)
{
    internal static async Task<TResult> ReadFromRepositoryAsync<TResult>(
        IFontManagementReader repository,
        long? batchId,
        Func<FontReportSnapshot?, Task<TResult>> readPage,
        CancellationToken cancellationToken)
    {
        var batch = batchId is long id
            ? await repository.GetAnalysisBatchAsync(id, cancellationToken).ConfigureAwait(false)
            : await repository.GetLatestCompleteAnalysisAsync(cancellationToken).ConfigureAwait(false);
        if (batch is null)
        {
            return await readPage(null).ConfigureAwait(false);
        }

        var files = await repository.LoadInventoryAsync(batch.GenerationId, cancellationToken).ConfigureAwait(false);
        return await readPage(new FontReportSnapshot(
            batch.Id,
            [.. batch.Relations.Select(row => new FontReportRelation(row.Id, row.LeftFaceId, row.RightFaceId, row.RelationshipKind, row.ReplacementDirection))],
            [.. batch.Groups.Select(group => new FontReportGroup(
                group.Id,
                group.ReclaimableBytes,
                [.. group.Members.Select(member => new FontReportMember(member.FaceId, member.FileId, member.Decision, member.Reason))]))],
            [.. files.Select(file => new FontReportFile(
                file.Id,
                file.Path,
                file.Health,
                file.Error,
                [.. file.Faces.Select(face => new FontReportFace(face.Id, face.Technology))]))],
            (relationIds, scoreFaceIds, _) =>
            {
                var relations = batch.Relations.Where(relation => relationIds.Contains(relation.Id)).ToArray();
                HashSet<long> pairFaceIds = [.. relations.SelectMany(relation => new[] { relation.LeftFaceId, relation.RightFaceId })];
                return Task.FromResult(new FontReportEvidence(
                    relations,
                    [.. files.SelectMany(file => file.Faces).Where(face => pairFaceIds.Contains(face.Id))],
                    [.. batch.FaceScores.Where(score => scoreFaceIds.Contains(score.FaceId))]));
            })).ConfigureAwait(false);
    }

    internal readonly record struct FontReportRelation(
        long Id,
        long LeftFaceId,
        long RightFaceId,
        FontRelationshipKind RelationshipKind,
        FontReplacementDirection ReplacementDirection);

    internal readonly record struct FontReportMember(long FaceId, long FileId, DuplicateGroupMemberDecision Decision, string Reason);

    internal readonly record struct FontReportFace(long Id, string Technology);

    internal sealed record FontReportGroup(long Id, long ReclaimableBytes, IReadOnlyList<FontReportMember> Members);

    internal sealed record FontReportFile(long Id, string Path, FontFileHealth Health, string? Error, IReadOnlyList<FontReportFace> Faces);

    internal sealed record FontReportEvidence(
        IReadOnlyList<FontRelationRecord> Relations,
        IReadOnlyList<FontFaceRecord> Faces,
        IReadOnlyList<FontFaceAnalysisRecord> Scores);
}
