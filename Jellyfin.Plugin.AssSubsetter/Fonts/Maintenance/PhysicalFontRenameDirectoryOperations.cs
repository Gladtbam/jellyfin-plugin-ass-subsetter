using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

/// <summary>Validates physical paths and changes one directory at a time.</summary>
public sealed class PhysicalFontRenameDirectoryOperations : IFontRenameDirectoryOperations
{
    private readonly Func<string, string?> _fileSystemIdentity;
    private readonly Dictionary<string, bool>? _previewComponents;
    private readonly Dictionary<string, string?>? _previewTargetFileSystems;
    private readonly PhysicalQuarantineFileSystem _files = new();
    private readonly PlatformPhysicalFileIdentityProvider _identity = new();

    /// <summary>Initializes a new instance of the <see cref="PhysicalFontRenameDirectoryOperations"/> class.</summary>
    /// <param name="fileSystemIdentity">An optional platform filesystem capability provider.</param>
    public PhysicalFontRenameDirectoryOperations(Func<string, string?>? fileSystemIdentity = null)
    {
        _fileSystemIdentity = fileSystemIdentity ?? NativeFontFileSystem.GetFileSystemIdentity;
    }

    private PhysicalFontRenameDirectoryOperations(Func<string, string?> fileSystemIdentity, bool preview)
        : this(fileSystemIdentity)
    {
        if (preview)
        {
            _previewComponents = new(FontPathComparison.Comparer);
            _previewTargetFileSystems = new(FontPathComparison.Comparer);
        }
    }

    /// <inheritdoc/>
    public IFontRenameDirectoryOperations CreatePreviewScope() => new PhysicalFontRenameDirectoryOperations(_fileSystemIdentity, preview: true);

    /// <inheritdoc/>
    public FontRenameDirectoryProbe Probe(string sourceFile, string sourceRoot, string targetDirectory)
    {
        try
        {
            if (!FontPathComparison.IsWithin(sourceFile, sourceRoot) || !FontPathComparison.IsWithin(targetDirectory, sourceRoot))
            {
                return new(false, false, "outside-source-root");
            }

            if (!_files.IsPhysicalPath(sourceFile, _previewComponents) || !_files.IsPhysicalPath(targetDirectory, _previewComponents))
            {
                return new(false, false, "linked-path");
            }

            if (!File.Exists(sourceFile))
            {
                return new(false, false, "source-missing");
            }

            if (_identity.GetLinkCount(sourceFile) != 1)
            {
                return new(false, false, "hard-link-or-unknown-link-count");
            }

            if ((File.GetAttributes(sourceFile) & FileAttributes.ReadOnly) != 0)
            {
                return new(false, false, "read-only");
            }

            string? targetIdentity = GetTargetFileSystem(targetDirectory, out string? targetError);
            if (targetError is not null)
            {
                return new(false, false, targetError);
            }

            string? sourceIdentity = _fileSystemIdentity(sourceFile);
            bool same = sourceIdentity is not null && targetIdentity is not null && string.Equals(sourceIdentity, targetIdentity, StringComparison.Ordinal);
            return new(true, same, same ? null : "different-filesystem");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new(false, false, "unsafe-directory: " + exception.Message);
        }
    }

    /// <inheritdoc/>
    public Task<FontRenameDirectoryResult> CreateDirectoryAsync(string path, string sourceRoot, FontDirectoryPolicyResolver policy, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? exclusion = ValidateDirectory(path, sourceRoot, policy);
        if (exclusion is not null)
        {
            return Task.FromResult(new FontRenameDirectoryResult(false, null, exclusion));
        }

        try
        {
            if (!Directory.Exists(Path.GetDirectoryName(Path.GetFullPath(path))))
            {
                return Task.FromResult(new FontRenameDirectoryResult(false, null, "parent-directory-missing"));
            }

            bool? created = NativeFontFileSystem.CreateSingleDirectory(path);
            return Task.FromResult(new FontRenameDirectoryResult(created is not null && _files.IsPhysicalPath(path), created, created is null ? "directory-create-failed" : null));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
            return Task.FromResult(new FontRenameDirectoryResult(false, null, "directory-create-failed: " + exception.Message));
        }
    }

    /// <inheritdoc/>
    public Task<FontRenameDirectoryResult> TryRemoveEmptyAsync(string path, string sourceRoot, FontDirectoryPolicyResolver policy, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string? exclusion = ValidateDirectory(path, sourceRoot, policy);
        if (exclusion is not null)
        {
            return Task.FromResult(new FontRenameDirectoryResult(false, null, exclusion));
        }

        try
        {
            if (!Directory.Exists(path))
            {
                return Task.FromResult(new FontRenameDirectoryResult(true, null, null));
            }

            if (Directory.EnumerateFileSystemEntries(path).Any())
            {
                return Task.FromResult(new FontRenameDirectoryResult(false, null, "directory-not-empty"));
            }

            exclusion = ValidateDirectory(path, sourceRoot, policy);
            if (exclusion is not null)
            {
                return Task.FromResult(new FontRenameDirectoryResult(false, null, exclusion));
            }

            Directory.Delete(path, recursive: false);
            return Task.FromResult(new FontRenameDirectoryResult(true, null, null));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult(new FontRenameDirectoryResult(false, null, "directory-remove-failed: " + exception.Message));
        }
    }

    private string? GetTargetFileSystem(string targetDirectory, out string? error)
    {
        error = null;
        string target = Path.GetFullPath(targetDirectory);
        if (_previewTargetFileSystems is not null && _previewTargetFileSystems.TryGetValue(target, out string? cached))
        {
            return cached;
        }

        string? ancestor = target;
        while (ancestor is not null && !Directory.Exists(ancestor))
        {
            if (File.Exists(ancestor))
            {
                error = "target-parent-is-file";
                return null;
            }

            ancestor = Path.GetDirectoryName(ancestor);
        }

        string? identity = null;
        if (ancestor is not null && (_previewTargetFileSystems is null || !_previewTargetFileSystems.TryGetValue(ancestor, out identity)))
        {
            identity = _fileSystemIdentity(ancestor);
        }

        if (_previewTargetFileSystems is not null)
        {
            _previewTargetFileSystems[target] = identity;
            if (ancestor is not null)
            {
                _previewTargetFileSystems[ancestor] = identity;
            }
        }

        return identity;
    }

    private string? ValidateDirectory(string path, string sourceRoot, FontDirectoryPolicyResolver policy)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetFullPath(sourceRoot);
        if (!Directory.Exists(root) || !FontPathComparison.IsWithin(fullPath, root) || FontPathComparison.Comparer.Equals(fullPath, root))
        {
            return "source-root-boundary";
        }

        if (!policy.GetScanRoots().Any(configured => (configured.Policy & FontDirectoryPolicy.Custom) != 0 && FontPathComparison.Comparer.Equals(Path.TrimEndingDirectorySeparator(configured.Path), Path.TrimEndingDirectorySeparator(root))))
        {
            return "source-root-changed";
        }

        if (policy.GetScanRoots().Any(configured => FontPathComparison.Comparer.Equals(configured.Path, fullPath)))
        {
            return "configured-root-boundary";
        }

        if ((policy.GetPolicy(fullPath) & FontDirectoryPolicy.Protected) != 0 || policy.GetOwningCustomRoot(fullPath) is null)
        {
            return "protected-or-not-custom";
        }

        return _files.IsPhysicalPath(fullPath) ? null : "linked-path";
    }
}
