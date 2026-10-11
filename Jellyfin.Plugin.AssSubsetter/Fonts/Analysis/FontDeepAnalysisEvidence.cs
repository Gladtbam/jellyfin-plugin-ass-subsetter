using System.Collections.Generic;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;

/// <summary>Contains deep Face evidence and updated retention quality for candidate physical files.</summary>
/// <param name="Faces">The revalidated Faces belonging to the selected files.</param>
/// <param name="Files">The matching revalidated physical-file retention evidence.</param>
public sealed record FontDeepAnalysisEvidence(
    IReadOnlyList<FontFaceEvidence> Faces,
    IReadOnlyList<FontRetentionFile> Files);
