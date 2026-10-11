using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

/// <summary>Abstracts no-overwrite filesystem transitions for deterministic lifecycle tests.</summary>
public interface IQuarantineFileSystem
{
    /// <summary>Checks whether an exact file path exists.</summary>
    /// <param name="path">The exact file path.</param>
    /// <returns><see langword="true"/> when the file exists.</returns>
    bool FileExists(string path);

    /// <summary>Checks whether an atomic move is available between two paths.</summary>
    /// <param name="sourcePath">The source path.</param>
    /// <param name="destinationPath">The generated destination path.</param>
    /// <returns><see langword="true"/> when both paths share a filesystem volume.</returns>
    bool IsSameVolume(string sourcePath, string destinationPath);

    /// <summary>Checks that no existing path component redirects filesystem access.</summary>
    /// <param name="path">The exact path whose existing ancestors are inspected.</param>
    /// <returns><see langword="true"/> only when every existing component is a physical non-link path.</returns>
    bool IsPhysicalPath(string path);

    /// <summary>Gets currently available bytes for the destination filesystem.</summary>
    /// <param name="destinationPath">A path on the destination filesystem.</param>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The available byte count.</returns>
    Task<long> GetAvailableSpaceAsync(string destinationPath, CancellationToken cancellationToken);

    /// <summary>Creates a batch-local directory without exposing a source-derived path.</summary>
    /// <param name="path">The generated directory path.</param>
    /// <param name="cancellationToken">A token that cancels creation.</param>
    /// <returns>A task representing directory creation.</returns>
    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken);

    /// <summary>Reads current length, modification time, and SHA-256 evidence.</summary>
    /// <param name="path">The exact file path.</param>
    /// <param name="cancellationToken">A token that cancels hashing.</param>
    /// <returns>The current evidence, or <see langword="null"/> when the file is absent.</returns>
    Task<QuarantineFileEvidence?> ReadEvidenceAsync(string path, CancellationToken cancellationToken);

    /// <summary>Copies a source into a new staging path without overwriting.</summary>
    /// <param name="sourcePath">The exact source path.</param>
    /// <param name="destinationPath">The generated staging path.</param>
    /// <param name="cancellationToken">A token that cancels copying.</param>
    /// <returns>A task representing the copy.</returns>
    Task CopyAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken);

    /// <summary>Atomically moves a file into an unoccupied destination path.</summary>
    /// <param name="sourcePath">The exact source path.</param>
    /// <param name="destinationPath">The exact generated or restored destination path.</param>
    /// <param name="cancellationToken">A token that cancels before the move.</param>
    /// <returns>A task representing the move.</returns>
    Task MoveAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken);

    /// <summary>Promotes a restore copy without overwriting, allowing a recoverable hard-link move when atomic rename is unsupported.</summary>
    /// <param name="sourcePath">The verified restore copy.</param>
    /// <param name="destinationPath">The unoccupied original path.</param>
    /// <param name="cancellationToken">A token that cancels before promotion.</param>
    /// <returns>A task representing restore promotion.</returns>
    Task MoveForRestoreAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
        => MoveAsync(sourcePath, destinationPath, cancellationToken);

    /// <summary>Deletes an exact path when lifecycle evidence authorizes deletion.</summary>
    /// <param name="path">The exact path.</param>
    /// <param name="cancellationToken">A token that cancels before deletion.</param>
    /// <returns>A task representing deletion.</returns>
    Task DeleteAsync(string path, CancellationToken cancellationToken);
}
