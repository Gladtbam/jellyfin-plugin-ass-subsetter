using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Api;

/// <summary>Projects persisted whole-file plans into report cards without hiding unrelated pair findings.</summary>
internal static class FontReportReplacementProjector
{
    internal static (IReadOnlyList<FontReportItemDto> Rows, IReadOnlySet<FontRelationRecord> CoveredRelations) Project(
        AnalysisBatch batch,
        IReadOnlyList<FontFileRecord> inventory)
    {
        Dictionary<long, FontFileRecord> files = inventory.ToDictionary(file => file.Id);
        var plans = new Dictionary<long, (DuplicateGroupRecord Group, FontReportFileDto[] Files)>();
        foreach (DuplicateGroupRecord group in batch.Groups)
        {
            if (group.Members.Any(member => !files.ContainsKey(member.FileId)))
            {
                continue;
            }

            FontReportFileDto[] summaries = group.Members.GroupBy(member => member.FileId)
                .Select(members =>
                {
                    FontFileRecord file = files[members.Key];
                    HashSet<long> candidates = members
                        .Where(member => member.Decision == DuplicateGroupMemberDecision.QuarantineCandidate)
                        .Select(member => member.FaceId).ToHashSet();
                    bool wholeFile = file.Faces.Count > 0 && file.Faces.All(face => candidates.Contains(face.Id));
                    return new FontReportFileDto(
                        file.Id,
                        file.Path,
                        file.Faces.Count,
                        wholeFile ? DuplicateGroupMemberDecision.QuarantineCandidate : DuplicateGroupMemberDecision.Retained,
                        members.Select(member => member.Reason).Where(reason => !string.IsNullOrWhiteSpace(reason)).Distinct().ToArray());
                })
                .OrderBy(file => file.Decision).ThenBy(file => file.FileId).ToArray();
            if (summaries.Any(file => file.Decision == DuplicateGroupMemberDecision.QuarantineCandidate)
                && summaries.Any(file => file.Decision == DuplicateGroupMemberDecision.Retained))
            {
                plans.Add(group.Id, (group, summaries));
            }
        }

        ILookup<long, long> groupsByFace = plans.Values
            .SelectMany(plan => plan.Group.Members.Select(member => (member.FaceId, GroupId: plan.Group.Id)))
            .ToLookup(item => item.FaceId, item => item.GroupId);
        var relationsByGroup = plans.Keys.ToDictionary(id => id, _ => new List<FontRelationRecord>());
        var covered = new HashSet<FontRelationRecord>();
        foreach (FontRelationRecord relation in batch.Relations)
        {
            if (relation.ReplacementDirection == FontReplacementDirection.None || !IsReplacement(relation.RelationshipKind))
            {
                continue;
            }

            foreach (long groupId in groupsByFace[relation.LeftFaceId].Intersect(groupsByFace[relation.RightFaceId]))
            {
                relationsByGroup[groupId].Add(relation);
                covered.Add(relation);
            }
        }

        var rows = new List<FontReportItemDto>(plans.Count);
        foreach (var plan in plans.Values)
        {
            FontRelationshipKind[] kinds = relationsByGroup[plan.Group.Id]
                .Select(relation => relation.RelationshipKind).Distinct().Order().ToArray();
            FontFileRecord[] related = plan.Files.Select(file => files[file.FileId]).ToArray();
            rows.Add(new FontReportItemDto(
                batch.Id,
                null,
                kinds.Length > 0 ? kinds[0] : null,
                false,
                FontReportAction.QuarantineCandidate,
                plan.Files.Select(file => file.FileId).ToArray(),
                related.SelectMany(file => file.Faces).Select(face => face.Id).Order().ToArray(),
                plan.Files.Select(file => file.Path).ToArray(),
                related.Select(file => Path.GetDirectoryName(file.Path) ?? string.Empty).Distinct(FontPathComparison.Comparer).ToArray(),
                related.SelectMany(file => file.Faces).Select(face => face.Technology).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct().ToArray(),
                null,
                null,
                [],
                [])
            {
                Replacement = new FontReportReplacementDto(plan.Group.Id, plan.Group.ReclaimableBytes, kinds, plan.Files)
            });
        }

        return (rows, covered);
    }

    private static bool IsReplacement(FontRelationshipKind kind) => kind is
        FontRelationshipKind.IdenticalFileHash or FontRelationshipKind.StrictFaceDuplicate or
        FontRelationshipKind.RenderingAndLayoutEquivalent or FontRelationshipKind.MetadataOnlyDifference or
        FontRelationshipKind.CoverageSuperset;
}
