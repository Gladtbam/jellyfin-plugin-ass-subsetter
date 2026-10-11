using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;

/// <summary>Contains deterministic retained, quarantine-candidate, and direct-group decisions.</summary>
/// <param name="RetainedFiles">Physical files that must remain available.</param>
/// <param name="QuarantineFiles">Physical files eligible for recoverable quarantine.</param>
/// <param name="Groups">Direct replacement groups without inferred transitive edges.</param>
public sealed record RetentionPlan(
    IReadOnlyList<RetentionFileDecision> RetainedFiles,
    IReadOnlyList<RetentionFileDecision> QuarantineFiles,
    IReadOnlyList<DuplicateGroupPlan> Groups)
{
    /// <summary>Gets the total safely reclaimable bytes.</summary>
    public long ReclaimableBytes => Groups.Sum(group => group.ReclaimableBytes);
}
