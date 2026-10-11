using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

/// <summary>Executes no-overwrite quarantine filesystem transitions on the host platform.</summary>
public sealed class PhysicalQuarantineFileSystem : IQuarantineFileSystem
{
    private readonly PlatformPhysicalFileIdentityProvider _identity = new();

    /// <summary>Checks whether an exact file path exists.</summary>
    /// <param name="path">The exact file path.</param>
    /// <returns><see langword="true"/> when the file exists.</returns>
    public bool FileExists(string path) => File.Exists(path);

    /// <summary>Checks whether both paths resolve under the same mounted drive root.</summary>
    /// <param name="sourcePath">The source path.</param>
    /// <param name="destinationPath">The destination path.</param>
    /// <returns><see langword="true"/> when both paths share the same mounted drive root.</returns>
    public bool IsSameVolume(string sourcePath, string destinationPath)
        => string.Equals(FindDrive(sourcePath).Name, FindDrive(destinationPath).Name, FontPathComparison.Comparison);

    /// <summary>Checks every existing path component for filesystem redirection.</summary>
    /// <param name="path">The exact path whose existing ancestors are inspected.</param>
    /// <returns><see langword="true"/> only when no existing component is a reparse point or symbolic link.</returns>
    public bool IsPhysicalPath(string path) => IsPhysicalPath(path, null);

    internal bool IsPhysicalPath(string path, IDictionary<string, bool>? componentCache)
    {
        try
        {
            for (FileSystemInfo? info = new FileInfo(Path.GetFullPath(path)); info is not null; info = info is FileInfo file ? file.Directory : ((DirectoryInfo)info).Parent)
            {
                bool physical;
                if (componentCache is null || !componentCache.TryGetValue(info.FullName, out physical))
                {
                    physical = info.LinkTarget is null && (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) == 0);
                    componentCache?.Add(info.FullName, physical);
                }

                if (!physical)
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Gets available free bytes for the destination drive.</summary>
    /// <param name="destinationPath">A path on the destination drive.</param>
    /// <param name="cancellationToken">A token that cancels before the query.</param>
    /// <returns>The available byte count.</returns>
    public Task<long> GetAvailableSpaceAsync(string destinationPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(FindDrive(destinationPath).AvailableFreeSpace);
    }

    /// <summary>Creates a generated batch-local directory.</summary>
    /// <param name="path">The generated directory path.</param>
    /// <param name="cancellationToken">A token that cancels before creation.</param>
    /// <returns>A completed task.</returns>
    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(path);
        return Task.CompletedTask;
    }

    /// <summary>Reads current file length, last-write time, and SHA-256 evidence.</summary>
    /// <param name="path">The exact file path.</param>
    /// <param name="cancellationToken">A token that cancels hashing.</param>
    /// <returns>The current evidence, or <see langword="null"/> when absent.</returns>
    public async Task<QuarantineFileEvidence?> ReadEvidenceAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var info = new FileInfo(path);
        long size = info.Length;
        DateTimeOffset modifiedUtc = info.LastWriteTimeUtc;
        string physicalIdentity = _identity.GetIdentity(path);
        uint? linkCount = _identity.GetLinkCount(path);
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var streamScope = stream.ConfigureAwait(false);
        byte[] digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        info.Refresh();
        if (!info.Exists || info.Length != size || info.LastWriteTimeUtc != modifiedUtc.UtcDateTime
            || !string.Equals(physicalIdentity, _identity.GetIdentity(path), StringComparison.Ordinal)
            || linkCount != _identity.GetLinkCount(path))
        {
            return null;
        }

        return new QuarantineFileEvidence(size, modifiedUtc, digest, physicalIdentity, linkCount);
    }

    /// <summary>Copies a source into a new staging path without overwriting.</summary>
    /// <param name="sourcePath">The source path.</param>
    /// <param name="destinationPath">The new staging path.</param>
    /// <param name="cancellationToken">A token that cancels copying.</param>
    /// <returns>A task representing the copy.</returns>
    public async Task CopyAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var sourceScope = source.ConfigureAwait(false);
        var destination = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destinationScope = destination.ConfigureAwait(false);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        RandomAccess.FlushToDisk(destination.SafeFileHandle);
    }

    /// <summary>Atomically moves a file without overwriting an occupied destination.</summary>
    /// <param name="sourcePath">The source path.</param>
    /// <param name="destinationPath">The unoccupied destination path.</param>
    /// <param name="cancellationToken">A token that cancels before movement.</param>
    /// <returns>A completed task.</returns>
    public Task MoveAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NativeFontFileSystem.MoveFileAtomically(sourcePath, destinationPath);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task MoveForRestoreAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NativeFontFileSystem.MoveRestoreFileWithoutOverwrite(sourcePath, destinationPath);
        return Task.CompletedTask;
    }

    /// <summary>Deletes an exact authorized path.</summary>
    /// <param name="path">The exact path.</param>
    /// <param name="cancellationToken">A token that cancels before deletion.</param>
    /// <returns>A completed task.</returns>
    public Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Delete(path);
        return Task.CompletedTask;
    }

    private static DriveInfo FindDrive(string path)
    {
        string fullPath = Path.GetFullPath(path);
        return DriveInfo.GetDrives()
            .Where(drive => fullPath.StartsWith(drive.RootDirectory.FullName, FontPathComparison.Comparison))
            .OrderByDescending(drive => drive.RootDirectory.FullName.Length)
            .FirstOrDefault()
            ?? throw new IOException($"No mounted drive contains path '{fullPath}'.");
    }
}
