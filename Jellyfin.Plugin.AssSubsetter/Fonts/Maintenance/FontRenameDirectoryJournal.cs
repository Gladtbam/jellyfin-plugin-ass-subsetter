using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

/// <summary>Replays directory intents and keeps their outcomes separate from file transitions.</summary>
internal sealed class FontRenameDirectoryJournal
{
    private readonly IFontRenameDirectoryOperations _directories;
    private readonly FontMaintenanceCoordinator _coordinator;
    private readonly Func<FontDirectoryPolicyResolver> _policy;
    private readonly Func<RenameManifest, Task> _persist;

    internal FontRenameDirectoryJournal(IFontRenameDirectoryOperations directories, FontMaintenanceCoordinator coordinator, Func<FontDirectoryPolicyResolver> policy, Func<RenameManifest, Task> persist)
    {
        _directories = directories;
        _coordinator = coordinator;
        _policy = policy;
        _persist = persist;
    }

    internal async Task<(RenameManifest Manifest, string? Error)> EnsureParentAsync(RenameManifest manifest, string destination, string sourceRoot)
    {
        string parent = Path.GetDirectoryName(destination)!;
        if (!FontPathComparison.IsWithin(parent, sourceRoot) || !Directory.Exists(sourceRoot))
        {
            return (manifest, "source-root-boundary");
        }

        var ancestors = new Stack<string>();
        for (string? path = parent; path is not null && !FontPathComparison.Comparer.Equals(path, sourceRoot); path = Path.GetDirectoryName(path))
        {
            ancestors.Push(path);
        }

        foreach (string path in ancestors)
        {
            var record = Find(manifest, path, RenameDirectoryAction.Create);
            if (record is null && Directory.Exists(path))
            {
                continue;
            }

            if (record is null)
            {
                record = new(path, sourceRoot, RenameDirectoryAction.Create, RenameDirectoryStatus.Planned, null, null);
                manifest = Put(manifest, record);
                await _persist(manifest).ConfigureAwait(false);
            }

            // A crash after creation cannot establish ownership from directory existence.
            FontRenameDirectoryResult result = record.Status == RenameDirectoryStatus.Planned && Directory.Exists(path)
                ? new(true, null, null)
                : await _directories.CreateDirectoryAsync(path, sourceRoot, _policy(), CancellationToken.None).ConfigureAwait(false);
            record = record with
            {
                Status = result.Completed ? RenameDirectoryStatus.Completed : RenameDirectoryStatus.Warning,
                CreatedByBatch = record.Status == RenameDirectoryStatus.Completed ? record.CreatedByBatch : result.CreatedByOperation,
                Error = result.Error
            };
            manifest = Put(manifest, record);
            await _persist(manifest).ConfigureAwait(false);
            if (!result.Completed)
            {
                return (manifest, result.Error ?? "directory-create-failed");
            }
        }

        return (manifest, null);
    }

    internal async Task<RenameManifest> CleanupAncestorsAsync(RenameManifest manifest, string filePath, string sourceRoot)
    {
        for (string? path = Path.GetDirectoryName(filePath); path is not null && FontPathComparison.IsWithin(path, sourceRoot) && !FontPathComparison.Comparer.Equals(path, sourceRoot); path = Path.GetDirectoryName(path))
        {
            manifest = await RemoveAsync(manifest, path, sourceRoot).ConfigureAwait(false);
            if (Find(manifest, path, RenameDirectoryAction.RemoveEmpty)!.Status != RenameDirectoryStatus.Completed)
            {
                break;
            }
        }

        return manifest;
    }

    internal async Task<RenameManifest> CleanupCreatedAsync(RenameManifest manifest, bool cancelled)
    {
        bool rollback = manifest.Batch.Kind == FontOperationKind.RollbackNames;
        foreach (var intent in manifest.Directories.Where(record => record.Action == RenameDirectoryAction.Create && record.Status == RenameDirectoryStatus.Planned).ToArray())
        {
            bool needed = manifest.Batch.RenameItems.Any(item => ((!cancelled && item.Status == RenameItemStatus.Pending) || item.Status is RenameItemStatus.Moving or RenameItemStatus.Temporary or RenameItemStatus.RollingBack)
                && FontPathComparison.IsWithin(rollback ? item.OldPath : item.NewPath, intent.Path));
            if (!needed)
            {
                manifest = Put(manifest, intent with { Status = RenameDirectoryStatus.Retained, Error = "directory-create-unused" });
                await _persist(manifest).ConfigureAwait(false);
            }
        }

        foreach (var created in manifest.Directories.Where(record => record.Action == RenameDirectoryAction.Create && record.CreatedByBatch == true).OrderByDescending(record => record.Path.Length).ToArray())
        {
            bool needed = manifest.Batch.RenameItems.Any(item => ((!cancelled && item.Status == RenameItemStatus.Pending) || item.Status is RenameItemStatus.Moving or RenameItemStatus.Temporary or RenameItemStatus.RollingBack)
                && FontPathComparison.IsWithin(rollback ? item.OldPath : item.NewPath, created.Path));
            if (!needed)
            {
                manifest = await RemoveAsync(manifest, created.Path, created.SourceRoot).ConfigureAwait(false);
            }
        }

        return manifest;
    }

    internal async Task<RenameManifest> ReplayCleanupAsync(RenameManifest manifest)
    {
        foreach (var record in manifest.Directories.Where(record => record.Action == RenameDirectoryAction.RemoveEmpty && record.Status is RenameDirectoryStatus.Planned or RenameDirectoryStatus.Warning).ToArray())
        {
            manifest = await RemoveAsync(manifest, record.Path, record.SourceRoot).ConfigureAwait(false);
        }

        return manifest;
    }

    private async Task<RenameManifest> RemoveAsync(RenameManifest manifest, string path, string sourceRoot)
    {
        var record = Find(manifest, path, RenameDirectoryAction.RemoveEmpty);
        if (record?.Status is RenameDirectoryStatus.Completed or RenameDirectoryStatus.Retained)
        {
            return manifest;
        }

        if (record is null)
        {
            record = new(path, sourceRoot, RenameDirectoryAction.RemoveEmpty, RenameDirectoryStatus.Planned, null, null);
            manifest = Put(manifest, record);
            await _persist(manifest).ConfigureAwait(false);
        }

        FontRenameDirectoryResult result = _coordinator.IsDirectoryInUse(path)
            ? new(false, null, "directory-in-use")
            : await _directories.TryRemoveEmptyAsync(path, sourceRoot, _policy(), CancellationToken.None).ConfigureAwait(false);
        record = record with
        {
            Status = result.Completed ? RenameDirectoryStatus.Completed
                : result.Error?.StartsWith("directory-remove-failed", StringComparison.Ordinal) == true ? RenameDirectoryStatus.Warning : RenameDirectoryStatus.Retained,
            Error = result.Error
        };
        manifest = Put(manifest, record);
        await _persist(manifest).ConfigureAwait(false);
        return manifest;
    }

    private static RenameDirectoryRecord? Find(RenameManifest manifest, string path, RenameDirectoryAction action)
        => manifest.Directories.FirstOrDefault(record => record.Action == action && FontPathComparison.Comparer.Equals(record.Path, path));

    private static RenameManifest Put(RenameManifest manifest, RenameDirectoryRecord record)
    {
        var records = manifest.Directories.ToList();
        int index = records.FindIndex(item => item.Action == record.Action && FontPathComparison.Comparer.Equals(item.Path, record.Path));
        if (index < 0)
        {
            records.Add(record);
        }
        else
        {
            records[index] = record;
        }

        return manifest with { Directories = records };
    }
}
