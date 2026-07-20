using System.Collections.Generic;

namespace Jellyfin.Plugin.AssSubsetter.Models;

/// <summary>
///     Format-neutral output of ASS font subsetting.
/// </summary>
/// <param name="AssContent">Rewritten ASS content without a Fonts section.</param>
/// <param name="Fonts">Separate subset font attachments.</param>
public sealed record SubsetArtifact(string AssContent, IReadOnlyList<SubsetFontAttachment> Fonts);
