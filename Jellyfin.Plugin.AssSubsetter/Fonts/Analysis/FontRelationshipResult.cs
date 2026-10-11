using System.Collections.Generic;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;

/// <summary>Reports the first proven relationship and any safe direct replacement direction.</summary>
/// <param name="Kind">The first relationship in the approved decision chain.</param>
/// <param name="LeftCanReplaceRight">Whether the left Face directly proves all capabilities required by the right Face.</param>
/// <param name="RightCanReplaceLeft">Whether the right Face directly proves all capabilities required by the left Face.</param>
/// <param name="EvidenceKeys">Stable evidence keys suitable for reports and diagnostics.</param>
/// <param name="DifferingCodePoints">Encoded code points whose normalized glyph behavior differs.</param>
public sealed record FontRelationshipResult(
    FontRelationshipKind Kind,
    bool LeftCanReplaceRight,
    bool RightCanReplaceLeft,
    IReadOnlyList<string> EvidenceKeys,
    IReadOnlyList<uint> DifferingCodePoints);
