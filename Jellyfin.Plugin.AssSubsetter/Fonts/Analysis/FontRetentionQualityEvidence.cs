using System.Collections.Generic;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;

/// <summary>Contains fixed quality signals and comparable version evidence for one Face.</summary>
/// <param name="Signals">The proven fixed quality subreasons.</param>
/// <param name="Version">The reliable comparable version, or <see langword="null"/>.</param>
public sealed record FontRetentionQualityEvidence(IReadOnlySet<FontQualitySignal> Signals, decimal? Version)
{
    /// <summary>Gets individual verification outcomes; unknown results never confer quality credit.</summary>
    public IReadOnlyDictionary<string, FontVerificationStatus> Verification { get; init; } = new Dictionary<string, FontVerificationStatus>();
}
