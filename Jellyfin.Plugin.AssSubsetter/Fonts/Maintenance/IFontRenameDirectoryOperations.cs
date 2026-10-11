using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

/// <summary>Physical path validation and single-directory transitions for font renaming.</summary>
public interface IFontRenameDirectoryOperations
{
    /// <summary>Creates a read-only scope whose evidence may be reused for one preview calculation.</summary>
    /// <returns>A fresh preview scope. Execution must use the original uncached operations.</returns>
    IFontRenameDirectoryOperations CreatePreviewScope() => this;

    /// <summary>Checks actual filesystem capabilities without creating directories.</summary>
    /// <param name="sourceFile">The existing source font.</param>
    /// <param name="sourceRoot">The owning physical custom root.</param>
    /// <param name="targetDirectory">The generated destination parent.</param>
    /// <returns>The physical path and filesystem evidence.</returns>
    FontRenameDirectoryProbe Probe(string sourceFile, string sourceRoot, string targetDirectory);

    /// <summary>Creates one missing directory with validated ancestors.</summary>
    /// <param name="path">The intended directory.</param>
    /// <param name="sourceRoot">The owning physical custom root.</param>
    /// <param name="policy">The current safety policy.</param>
    /// <param name="cancellationToken">Cancels before mutation.</param>
    /// <returns>The result including atomic creation ownership.</returns>
    Task<FontRenameDirectoryResult> CreateDirectoryAsync(string path, string sourceRoot, FontDirectoryPolicyResolver policy, CancellationToken cancellationToken);

    /// <summary>Removes one empty directory without recursive content deletion.</summary>
    /// <param name="path">The intended directory.</param>
    /// <param name="sourceRoot">The owning physical custom root.</param>
    /// <param name="policy">The current safety policy.</param>
    /// <param name="cancellationToken">Cancels before mutation.</param>
    /// <returns>The deleted or retained result.</returns>
    Task<FontRenameDirectoryResult> TryRemoveEmptyAsync(string path, string sourceRoot, FontDirectoryPolicyResolver policy, CancellationToken cancellationToken);
}
