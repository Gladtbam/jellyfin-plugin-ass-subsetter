using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;

/// <summary>Loads complete bounded deep evidence for one persisted inventory generation.</summary>
public interface IFontAnalysisEvidenceSource
{
    /// <summary>Loads Face and file evidence that belongs exclusively to the requested generation.</summary>
    /// <param name="generationId">The inventory generation to analyze.</param>
    /// <param name="cancellationToken">A token that cancels evidence loading.</param>
    /// <returns>The immutable analysis snapshot.</returns>
    Task<FontAnalysisSnapshot> LoadAsync(long generationId, CancellationToken cancellationToken);
}
