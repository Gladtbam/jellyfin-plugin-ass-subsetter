using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance.Naming;
using Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

/// <summary>Stores immutable rename intents and append-only durable transition snapshots.</summary>
public sealed class FileRenameManifestStore
{
    private readonly string _root;

    /// <summary>Initializes a new instance of the <see cref="FileRenameManifestStore"/> class.</summary>
    /// <param name="root">The fixed internal rename-manifest directory.</param>
    public FileRenameManifestStore(string root) => _root = Path.GetFullPath(root);

    /// <summary>Durably appends a complete state snapshot without overwriting the immutable intent.</summary>
    /// <param name="manifest">The immutable request and latest batch state.</param>
    /// <param name="cancellationToken">Cancels before publication.</param>
    /// <returns>A task representing durable publication.</returns>
    public async Task SaveAsync(RenameManifest manifest, CancellationToken cancellationToken)
    {
        Validate(manifest);
        string directory = BatchDirectory(manifest.Batch.Id);
        Directory.CreateDirectory(directory);
        string intentPath = Path.Combine(directory, "intent.json");
        if (!File.Exists(intentPath))
        {
            await WriteNewAsync(intentPath, manifest, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            RenameManifest intent = JsonSerializer.Deserialize<RenameManifest>(await File.ReadAllBytesAsync(intentPath, cancellationToken).ConfigureAwait(false))!;
            ValidateIdentity(intent, manifest);
        }

        string? previousPath = Directory.EnumerateFiles(directory, "state-*.json").OrderDescending(StringComparer.Ordinal).FirstOrDefault();
        if (previousPath is not null)
        {
            var previous = JsonSerializer.Deserialize<RenameManifest>(await File.ReadAllBytesAsync(previousPath, cancellationToken).ConfigureAwait(false))!;
            ValidateDirectoryProgress(previous, manifest);
        }

        long sequence = Directory.EnumerateFiles(directory, "state-*.json")
            .Select(path => long.Parse(Path.GetFileNameWithoutExtension(path).AsSpan(6), CultureInfo.InvariantCulture))
            .DefaultIfEmpty(0).Max() + 1;
        await WriteNewAsync(Path.Combine(directory, "state-" + sequence.ToString("D20", CultureInfo.InvariantCulture) + ".json"), manifest, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads the latest validated snapshot for every durable rename intent.</summary>
    /// <param name="cancellationToken">Cancels file reads.</param>
    /// <returns>Validated batch manifests in original execution order.</returns>
    public async Task<IReadOnlyList<RenameManifest>> LoadAllAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_root))
        {
            return [];
        }

        var manifests = new List<RenameManifest>();
        foreach (string directory in Directory.EnumerateDirectories(_root).Order(StringComparer.Ordinal))
        {
            string intentPath = Path.Combine(directory, "intent.json");
            if (!File.Exists(intentPath))
            {
                continue;
            }

            RenameManifest intent = JsonSerializer.Deserialize<RenameManifest>(await File.ReadAllBytesAsync(intentPath, cancellationToken).ConfigureAwait(false))!;
            Validate(intent);
            if (!string.Equals(directory, BatchDirectory(intent.Batch.Id), StringComparison.Ordinal))
            {
                throw new InvalidDataException("A rename manifest is outside its generated batch directory.");
            }

            RenameManifest latest = intent;
            foreach (string statePath in Directory.EnumerateFiles(directory, "state-*.json").Order(StringComparer.Ordinal))
            {
                RenameManifest next = JsonSerializer.Deserialize<RenameManifest>(await File.ReadAllBytesAsync(statePath, cancellationToken).ConfigureAwait(false))!;
                ValidateIdentity(intent, next);
                ValidateDirectoryProgress(latest, next);
                latest = next;
            }

            manifests.Add(latest);
        }

        return manifests;
    }

    private static async Task WriteNewAsync(string path, RenameManifest manifest, CancellationToken cancellationToken)
    {
        string temporary = path + ".tmp";
        var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await using (stream.ConfigureAwait(false))
        {
            await JsonSerializer.SerializeAsync(stream, manifest, cancellationToken: cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            RandomAccess.FlushToDisk(stream.SafeFileHandle);
        }

        cancellationToken.ThrowIfCancellationRequested();
        File.Move(temporary, path, false);
    }

    private static void ValidateIdentity(RenameManifest original, RenameManifest current)
    {
        Validate(current);
        if (original.Request != current.Request
            || original.Batch.Id != current.Batch.Id || original.Batch.Kind != current.Batch.Kind
            || original.Batch.RenameItems.Count != current.Batch.RenameItems.Count
            || original.Rules != current.Rules || original.RuleFingerprint != current.RuleFingerprint)
        {
            throw new InvalidDataException("A rename snapshot changed its immutable authorization.");
        }

        for (int index = 0; index < original.Batch.RenameItems.Count; index++)
        {
            RenameItemRecord before = original.Batch.RenameItems[index];
            RenameItemRecord after = current.Batch.RenameItems[index];
            if (JsonSerializer.Serialize(before with { Status = after.Status, Error = after.Error }) != JsonSerializer.Serialize(after))
            {
                throw new InvalidDataException("A rename snapshot changed its immutable file evidence.");
            }
        }
    }

    private static void ValidateDirectoryProgress(RenameManifest previous, RenameManifest current)
    {
        foreach (RenameDirectoryRecord before in previous.Directories)
        {
            var after = current.Directories.SingleOrDefault(record => record.Action == before.Action && FontPathComparison.Comparer.Equals(record.Path, before.Path));
            if (after is null || before.Path != after.Path || before.SourceRoot != after.SourceRoot
                || (before.Status != RenameDirectoryStatus.Planned && before.CreatedByBatch != after.CreatedByBatch)
                || (before.Status is RenameDirectoryStatus.Completed or RenameDirectoryStatus.Retained && before != after)
                || (before.Status != RenameDirectoryStatus.Planned && after.Status == RenameDirectoryStatus.Planned))
            {
                throw new InvalidDataException("A rename snapshot changed a confirmed directory result.");
            }
        }

        if (current.Directories.Skip(previous.Directories.Count).Any(record => record.Status != RenameDirectoryStatus.Planned))
        {
            throw new InvalidDataException("Directory intent must be persisted before its result.");
        }
    }

    private static void Validate(RenameManifest manifest)
    {
        if (manifest.Batch.Id <= 0 || manifest.Request.Id != manifest.Batch.OperationRequestId
            || manifest.Request.Kind != manifest.Batch.Kind
            || manifest.Batch.Kind is not FontOperationKind.NormalizeNames and not FontOperationKind.RollbackNames
            || manifest.Rules is null || manifest.Rules.FormatVersion != FontNamingRules.CurrentFormatVersion
            || manifest.RuleFingerprint != manifest.Rules.GetFingerprint())
        {
            throw new InvalidDataException("Invalid rename manifest identity, rule format, or fingerprint.");
        }

        if (manifest.Batch.Kind == FontOperationKind.NormalizeNames)
        {
            FontRenameExecutionPayload authorization;
            try
            {
                authorization = FontOperationRequestPayloadCodec.ParseRenameExecution(manifest.Request.RequestJson);
            }
            catch (Exception exception) when (exception is ArgumentException or JsonException)
            {
                throw new InvalidDataException("Invalid saved filename authorization.", exception);
            }

            if (authorization.Rules != manifest.Rules || authorization.RuleFingerprint != manifest.RuleFingerprint
                || JsonSerializer.Serialize(authorization.Items) != JsonSerializer.Serialize(manifest.Batch.RenameItems.Select(item => item with { TemporaryPath = string.Empty, Status = RenameItemStatus.Pending, Error = null }).ToArray()))
            {
                throw new InvalidDataException("The rename journal differs from its immutable authorization.");
            }
        }

        foreach (RenameItemRecord item in manifest.Batch.RenameItems)
        {
            string? parent = Path.GetDirectoryName(Path.GetFullPath(item.OldPath));
            if (item.Evidence.Sha256.Length != 32 || item.Evidence.Id != item.FileId || item.Evidence.Path != item.OldPath
                || !Path.IsPathFullyQualified(item.SourceRoot) || !Path.IsPathFullyQualified(item.OldPath) || !Path.IsPathFullyQualified(item.NewPath)
                || !FontPathComparison.IsWithin(item.OldPath, item.SourceRoot) || !FontPathComparison.IsWithin(item.NewPath, item.SourceRoot)
                || !string.Equals(Path.GetExtension(item.OldPath), Path.GetExtension(item.NewPath), StringComparison.OrdinalIgnoreCase) || Path.GetExtension(item.NewPath) is not ".ttf" and not ".otf" and not ".ttc" and not ".otc"
                || !string.Equals(item.TemporaryPath, Path.Combine(parent!, ".ass-subsetter-rename-" + manifest.Batch.Id.ToString(CultureInfo.InvariantCulture) + "-" + item.FileId.ToString(CultureInfo.InvariantCulture) + ".tmp"), StringComparison.Ordinal))
            {
                throw new InvalidDataException("A rename manifest contains invalid root-bounded file evidence.");
            }
        }

        bool rollback = manifest.Batch.Kind == FontOperationKind.RollbackNames;
        var directoryKeys = new HashSet<string>(FontPathComparison.Comparer);
        foreach (RenameDirectoryRecord record in manifest.Directories)
        {
            bool related = manifest.Batch.RenameItems.Any(item => item.SourceRoot == record.SourceRoot
                && (FontPathComparison.IsWithin(Path.GetDirectoryName(item.OldPath)!, record.Path) || FontPathComparison.IsWithin(Path.GetDirectoryName(item.NewPath)!, record.Path)));
            bool target = manifest.Batch.RenameItems.Any(item => item.SourceRoot == record.SourceRoot && FontPathComparison.IsWithin(Path.GetDirectoryName(rollback ? item.OldPath : item.NewPath)!, record.Path));
            bool owned = manifest.Directories.Any(created => created.Action == RenameDirectoryAction.Create && created.Path == record.Path && created.CreatedByBatch == true);
            if (!Enum.IsDefined(record.Action) || !Enum.IsDefined(record.Status) || !Path.IsPathFullyQualified(record.Path)
                || !Path.IsPathFullyQualified(record.SourceRoot) || !FontPathComparison.IsWithin(record.Path, record.SourceRoot)
                || FontPathComparison.Comparer.Equals(record.Path, record.SourceRoot) || !related
                || !directoryKeys.Add(record.Action + ":" + record.Path)
                || (record.Action == RenameDirectoryAction.Create && !target)
                || (record.Action == RenameDirectoryAction.RemoveEmpty && (record.CreatedByBatch is not null || (!manifest.Rules.RemoveEmptyDirectories && !owned)))
                || (record.Status == RenameDirectoryStatus.Planned && record.CreatedByBatch is not null))
            {
                throw new InvalidDataException("A rename manifest contains an unauthorized directory action.");
            }
        }
    }

    private string BatchDirectory(long id) => Path.Combine(_root, id.ToString("D20", CultureInfo.InvariantCulture));
}
