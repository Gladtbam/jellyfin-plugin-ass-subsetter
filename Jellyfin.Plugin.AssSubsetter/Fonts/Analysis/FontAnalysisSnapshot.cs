using System.Collections.Generic;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;

/// <summary>Contains deep Face evidence and the matching immutable physical-file policy snapshot.</summary>
/// <param name="Faces">The deep evidence for the requested inventory generation.</param>
/// <param name="Policy">The physical-file safety and quality snapshot for the same generation.</param>
public sealed record FontAnalysisSnapshot(
    IReadOnlyList<FontFaceEvidence> Faces,
    DirectoryPolicySnapshot Policy);
