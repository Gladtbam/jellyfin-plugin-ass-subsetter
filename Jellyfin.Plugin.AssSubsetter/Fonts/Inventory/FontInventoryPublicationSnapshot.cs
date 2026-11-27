using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

internal sealed record FontInventoryPublicationSnapshot(long GenerationId, FontAnalysisInputSnapshot Input, IReadOnlyList<FontInventoryPublicationSnapshot.FileEvidence> Files)
{
    internal static FontInventoryPublicationSnapshot Capture(long generationId, FontAnalysisInputSnapshot input, IReadOnlyList<FontFileRecord> files)
    {
        FileEvidence[] evidence = [.. files.Select(FileEvidence.Capture)];
        return new(generationId, input, evidence);
    }

    internal readonly record struct FileEvidence(
        string Path,
        string PhysicalIdentity,
        FontLinkKind LinkKind,
        FontDirectoryPolicy Policy,
        IReadOnlyList<string> Aliases,
        long Size,
        DateTimeOffset ModifiedUtc,
        ReadOnlyMemory<byte> Sha256)
    {
        internal static FileEvidence Capture(FontFileRecord file) => new(
            file.Path,
            file.PhysicalIdentity,
            file.LinkKind,
            file.DirectoryPolicy,
            [.. file.PathAliases],
            file.Size,
            file.ModifiedUtc,
            file.Sha256.ToArray());
    }
}
