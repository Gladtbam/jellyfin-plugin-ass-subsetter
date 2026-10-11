using System.Collections.Generic;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;

/// <summary>Provides the immutable physical-file and directory-policy snapshot used by retention selection.</summary>
/// <param name="Files">The physical files participating in the analyzed inventory generation.</param>
public sealed record DirectoryPolicySnapshot(IReadOnlyList<FontRetentionFile> Files);
