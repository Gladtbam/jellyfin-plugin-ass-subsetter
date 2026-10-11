using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

/// <summary>Stores recovery manifests atomically under a fixed plugin-internal quarantine root.</summary>
public sealed class FileQuarantineManifestStore : IQuarantineManifestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly string _manifestRoot;
    private readonly string _quarantineRoot;

    /// <summary>Initializes a new instance of the <see cref="FileQuarantineManifestStore"/> class.</summary>
    /// <param name="root">The fixed plugin-internal quarantine root.</param>
    public FileQuarantineManifestStore(string root)
        : this(root, root)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="FileQuarantineManifestStore"/> class with separate storage roots.</summary>
    /// <param name="manifestRoot">The fixed plugin-internal recovery-manifest root.</param>
    /// <param name="quarantineRoot">The fixed plugin-internal quarantined-file root.</param>
    public FileQuarantineManifestStore(string manifestRoot, string quarantineRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(quarantineRoot);
        _manifestRoot = Path.GetFullPath(manifestRoot);
        _quarantineRoot = Path.GetFullPath(quarantineRoot);
    }

    /// <summary>Atomically writes the complete manifest to its generated batch path.</summary>
    /// <param name="manifest">The complete manifest.</param>
    /// <param name="cancellationToken">A token that cancels before atomic promotion.</param>
    /// <returns>A task representing durable persistence.</returns>
    public async Task SaveAsync(QuarantineManifest manifest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        string manifestDirectory = ManifestBatchDirectory(manifest.BatchId);
        ValidateGeneratedItemPaths(manifest, QuarantineBatchDirectory(manifest.BatchId));
        Directory.CreateDirectory(manifestDirectory);
        string finalPath = Path.Combine(manifestDirectory, "manifest.json");
        string temporaryPath = Path.Combine(manifestDirectory, "manifest.tmp");
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        try
        {
            var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(json, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                RandomAccess.FlushToDisk(stream.SafeFileHandle);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, finalPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    /// <summary>Loads and validates every manifest stored at a generated batch path.</summary>
    /// <param name="cancellationToken">A token that cancels enumeration.</param>
    /// <returns>The valid manifests ordered by batch identifier.</returns>
    public async Task<IReadOnlyList<QuarantineManifest>> LoadAllAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_manifestRoot))
        {
            return [];
        }

        var manifests = new List<QuarantineManifest>();
        foreach (string path in Directory.EnumerateFiles(_manifestRoot, "manifest.json", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var streamScope = stream.ConfigureAwait(false);
            QuarantineManifest? manifest = await JsonSerializer.DeserializeAsync<QuarantineManifest>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            if (manifest is null)
            {
                continue;
            }

            string manifestDirectory = ManifestBatchDirectory(manifest.BatchId);
            if (!string.Equals(Path.GetFullPath(path), Path.Combine(manifestDirectory, "manifest.json"), FontPathComparison.Comparison))
            {
                continue;
            }

            ValidateGeneratedItemPaths(manifest, QuarantineBatchDirectory(manifest.BatchId));
            manifests.Add(manifest);
        }

        return manifests.OrderBy(manifest => manifest.BatchId).ToArray();
    }

    private void ValidateGeneratedItemPaths(QuarantineManifest manifest, string batchDirectory)
    {
        manifest.ValidateCurrentFormat();

        string itemRoot = Path.Combine(batchDirectory, "items");
        foreach (QuarantineManifestItem item in manifest.Items)
        {
            string stem = item.FileId.ToString("D20", CultureInfo.InvariantCulture);
            if (!string.Equals(Path.GetFullPath(item.StagingPath), Path.Combine(itemRoot, stem + ".stage"), FontPathComparison.Comparison)
                || !string.Equals(Path.GetFullPath(item.QuarantinePath), Path.Combine(itemRoot, stem + ".font"), FontPathComparison.Comparison))
            {
                throw new InvalidDataException("A manifest contains a non-generated staging or quarantine path.");
            }
        }
    }

    private string ManifestBatchDirectory(long batchId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchId);
        return Path.Combine(_manifestRoot, batchId.ToString("D20", CultureInfo.InvariantCulture));
    }

    private string QuarantineBatchDirectory(long batchId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchId);
        return Path.Combine(_quarantineRoot, batchId.ToString("D20", CultureInfo.InvariantCulture));
    }
}
