using System.Collections.Generic;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Api;

/// <summary>Summarizes a verified whole-file replacement plan; full Face evidence is loaded on demand.</summary>
/// <param name="GroupId">The owning persisted duplicate group.</param>
/// <param name="ReclaimableBytes">The recoverable size of the verified candidates.</param>
/// <param name="RelationshipKinds">The replacement relationships represented by this report card.</param>
/// <param name="Files">The retained and candidate physical files.</param>
public sealed record FontReportReplacementDto(
    long GroupId,
    long ReclaimableBytes,
    IReadOnlyList<FontRelationshipKind> RelationshipKinds,
    IReadOnlyList<FontReportFileDto> Files);
