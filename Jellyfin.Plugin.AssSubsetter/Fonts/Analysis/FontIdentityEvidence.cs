using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;

/// <summary>Describes normalized name and version evidence used to compare font identities.</summary>
/// <param name="PostScriptName">The normalized Name ID 6 value.</param>
/// <param name="FamilyName">The normalized family selected from the first complete supported pair.</param>
/// <param name="SubfamilyName">The normalized subfamily selected with <paramref name="FamilyName"/>.</param>
/// <param name="FamilyNameId">The selected family Name ID, or zero when no pair exists.</param>
/// <param name="SubfamilyNameId">The selected subfamily Name ID, or zero when no pair exists.</param>
/// <param name="FullName">The normalized Name ID 4 value.</param>
/// <param name="UniqueId">The normalized supporting Name ID 3 value.</param>
/// <param name="PrimaryIdentity">The first available identity value in the approved evidence order.</param>
/// <param name="AllNames">All normalized identity and lookup names.</param>
/// <param name="VendorId">The normalized OS/2 vendor identifier.</param>
/// <param name="Weight">The declared weight class.</param>
/// <param name="Width">The declared width class.</param>
/// <param name="Slant">The normalized slant value.</param>
/// <param name="Version">The combined version evidence.</param>
public sealed record FontIdentityEvidence(
    string? PostScriptName,
    string? FamilyName,
    string? SubfamilyName,
    ushort FamilyNameId,
    ushort SubfamilyNameId,
    string? FullName,
    string? UniqueId,
    string? PrimaryIdentity,
    IReadOnlySet<string> AllNames,
    string? VendorId,
    int Weight,
    int Width,
    int Slant,
    FontVersionEvidence Version)
{
    /// <summary>Gets every normalized family name from legacy, typographic, and WWS name records.</summary>
    public IReadOnlySet<string> FamilyNames { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Gets every normalized subfamily name from legacy, typographic, and WWS name records.</summary>
    public IReadOnlySet<string> SubfamilyNames { get; init; } = new HashSet<string>(StringComparer.Ordinal);
}
