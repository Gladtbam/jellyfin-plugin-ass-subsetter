using System.Collections.Generic;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance.Naming;

/// <summary>A finalized stem or directory hierarchy with provenance.</summary>
/// <param name="Components">The safe components, without extension.</param>
/// <param name="Evidence">The fields and selections actually used.</param>
/// <param name="SkipReason">The reason the font could not be named.</param>
public sealed record FontTemplateResult(IReadOnlyList<string> Components, FontNamingEvidence Evidence, string? SkipReason)
{
    /// <summary>Gets the contiguous suffix fields at the end of the filename.</summary>
    public string ProtectedSuffix { get; init; } = string.Empty;

    /// <summary>Gets a value indicating whether the folder configuration was blank.</summary>
    public bool PreserveSourceDirectory { get; init; }
}
