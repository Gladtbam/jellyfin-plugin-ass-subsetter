using System.Collections.Generic;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Api;

/// <summary>Describes a physical file's verified disposition without repeating its Face evidence.</summary>
/// <param name="FileId">The persisted physical-file identifier.</param>
/// <param name="Path">The physical path rendered as text.</param>
/// <param name="FaceCount">The number of Faces in the physical file.</param>
/// <param name="Decision">The whole-file retention decision.</param>
/// <param name="Reasons">The safe persisted retention rationale.</param>
public sealed record FontReportFileDto(
    long FileId,
    string Path,
    int FaceCount,
    DuplicateGroupMemberDecision Decision,
    IReadOnlyList<string> Reasons);
