using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;

/// <summary>Loads expensive per-character and retention-quality evidence only for candidate physical files.</summary>
public interface IFontDeepAnalysisEvidenceSource
{
    /// <summary>Revalidates and loads the selected candidate files from one published inventory generation.</summary>
    /// <param name="generationId">The currently published inventory generation.</param>
    /// <param name="fileIds">The distinct candidate physical-file identifiers.</param>
    /// <param name="cancellationToken">A token that cancels file reading or glyph evidence construction.</param>
    /// <returns>The replacement deep Face and file-quality evidence.</returns>
    Task<FontDeepAnalysisEvidence> LoadFilesAsync(
        long generationId,
        IReadOnlyList<long> fileIds,
        CancellationToken cancellationToken);

    /// <summary>Loads selected files with per-character fingerprints for the Faces that need them.</summary>
    /// <param name="generationId">The inventory generation being analyzed.</param>
    /// <param name="fileIds">The distinct candidate physical-file identifiers.</param>
    /// <param name="glyphFaceIds">The candidate Faces requiring full per-character evidence.</param>
    /// <param name="cancellationToken">A token that cancels load and analysis.</param>
    /// <returns>The validated Face and file-quality evidence.</returns>
    internal Task<FontDeepAnalysisEvidence> LoadFilesAsync(
        long generationId,
        IReadOnlyList<long> fileIds,
        IReadOnlySet<long> glyphFaceIds,
        CancellationToken cancellationToken) => LoadFilesAsync(generationId, fileIds, cancellationToken);
}
