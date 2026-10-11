using System.Collections.Generic;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

/// <summary>Contains immutable authorization and the latest durable rename state.</summary>
/// <param name="Request">The immutable authorized operation.</param>
/// <param name="Batch">The latest durable per-item states.</param>
/// <param name="RefreshRequired">Whether a moved path still requires catalog refresh.</param>
public sealed record RenameManifest(OperationRequest Request, MaintenanceBatch Batch, bool RefreshRequired)
{
    /// <summary>Gets the immutable rules used for this execution.</summary>
    public FontNamingRules Rules { get; init; } = new(FontNamingRules.DefaultFileNameTemplate, string.Empty, false);

    /// <summary>Gets the immutable rule digest.</summary>
    public string RuleFingerprint { get; init; } = string.Empty;

    /// <summary>Gets the durable directory intent and result journal.</summary>
    public IReadOnlyList<RenameDirectoryRecord> Directories { get; init; } = [];
}
