using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

/// <summary>
/// Builds a complete SQLite-backed font inventory generation without exposing it before analysis.
/// </summary>
public sealed class FontInventoryScanner : IDisposable
{
    private readonly IFontRepository _repository;
    private readonly Func<FontDirectoryPolicyResolver> _policyFactory;
    private readonly IPhysicalFileIdentityProvider _identityProvider;
    private readonly ILogger<FontInventoryScanner> _logger;
    private readonly SemaphoreSlim _scanGate = new(1, 1);

    /// <summary>Initializes a new instance of the <see cref="FontInventoryScanner"/> class.</summary>
    /// <param name="repository">The durable generation repository.</param>
    /// <param name="policyFactory">Creates policy from the current plugin configuration.</param>
    /// <param name="identityProvider">Provides hard-link physical identity evidence.</param>
    /// <param name="logger">The scan diagnostic sink.</param>
    public FontInventoryScanner(
        IFontRepository repository,
        Func<FontDirectoryPolicyResolver> policyFactory,
        IPhysicalFileIdentityProvider identityProvider,
        ILogger<FontInventoryScanner> logger)
    {
        _repository = repository;
        _policyFactory = policyFactory;
        _identityProvider = identityProvider;
        _logger = logger;
    }

    /// <summary>Scans every configured root into an unpublished generation for analysis.</summary>
    /// <param name="progress">Receives percentage completion from zero through one hundred.</param>
    /// <param name="forceRebuild">Forces rebuilding derived data even when the inputs match.</param>
    /// <param name="cancellationToken">Cancels before publication, retaining the previous generation.</param>
    /// <returns>The identifier of the staged inventory generation.</returns>
    public async Task<FontInventoryScanResult> StageAsync(IProgress<double>? progress = null, bool forceRebuild = false, CancellationToken cancellationToken = default)
    {
        await _scanGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            progress?.Report(0);
            _logger.LogInformation("[AssSubsetter] Font inventory scan started. ForceRebuild: {ForceRebuild}.", forceRebuild);
            await _repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
            FontDirectoryPolicyResolver policy = _policyFactory();
            IReadOnlyList<FontFileRecord> previous = await _repository.LoadPublishedInventoryForReuseAsync(cancellationToken).ConfigureAwait(false);
            var stopwatch = Stopwatch.StartNew();
            (IReadOnlyList<FontFileRecord> records, int reusedCount) = await ObserveAsync(policy, previous, progress, cancellationToken).ConfigureAwait(false);
            FontAnalysisInputSnapshot input = FontInventoryInputComparer.Capture(records, policy, PhysicalFontAnalysisEvidenceSource.AnalysisCompatibilityKey);
            FontAnalysisInputSnapshot? publishedInput = await _repository.GetPublishedAnalysisInputAsync(cancellationToken).ConfigureAwait(false);
            var reasons = FontInventoryInputComparer.GetChangeReasons(publishedInput, input).ToList();
            AnalysisBatch? analysis;
            try
            {
                analysis = await _repository.GetLatestCompleteAnalysisAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                analysis = null;
            }

            if (forceRebuild)
            {
                reasons.Add("derived-data-rebuild");
            }

            if (analysis is null && !reasons.Contains("initial-scan"))
            {
                reasons.Add("analysis-missing");
            }

            progress?.Report(93);
            bool changed = reasons.Count > 0;
            long generationId = changed ? await _repository.BeginGenerationAsync(input, reasons, cancellationToken).ConfigureAwait(false) : analysis!.GenerationId;
            if (changed)
            {
                await _repository.WriteInventoryAsync(generationId, records.Select(file => file with { GenerationId = generationId }).ToArray(), cancellationToken).ConfigureAwait(false);
            }

            progress?.Report(98);
            await _repository.SavePerformanceSampleAsync(
                new PerformanceSample
                {
                    GenerationId = generationId,
                    Phase = "inventory-scan",
                    Duration = stopwatch.Elapsed,
                    ItemCount = records.Count,
                    ReusedCount = reusedCount,
                    RecordedUtc = DateTimeOffset.UtcNow
                },
                cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("[AssSubsetter] Font inventory scan completed. GenerationId: {GenerationId}; Files: {FileCount}; Reused: {ReusedCount}; Unclassifiable: {UnclassifiableCount}; HasChanges: {HasChanges}; ChangeReasons: {ChangeReasons}; ElapsedMs: {ElapsedMs}.", generationId, records.Count, reusedCount, records.Count(file => file.Health == FontFileHealth.Unclassifiable), changed, string.Join(",", reasons), stopwatch.ElapsedMilliseconds);
            progress?.Report(100);
            return new(generationId, changed, input, reasons);
        }
        finally
        {
            _scanGate.Release();
        }
    }

    /// <summary>Refuses publication if the captured files or configuration drifted during analysis.</summary>
    /// <param name="scan">The complete scan to publish.</param>
    /// <param name="cancellationToken">Cancels validation before publication.</param>
    /// <param name="progress">Optional progress across the final file observation.</param>
    /// <returns>A task representing validation.</returns>
    public async Task ValidatePublicationInputsAsync(FontInventoryScanResult scan, CancellationToken cancellationToken, IProgress<double>? progress = null)
    {
        FontDirectoryPolicyResolver policy = _policyFactory();
        IReadOnlyList<FontFileRecord> files = await _repository.LoadInventoryAsync(scan.GenerationId, cancellationToken).ConfigureAwait(false);
        (IReadOnlyList<FontFileRecord> observed, _) = await ObserveAsync(policy, files, progress, cancellationToken).ConfigureAwait(false);
        if (FontInventoryInputComparer.Capture(observed, policy, PhysicalFontAnalysisEvidenceSource.AnalysisCompatibilityKey) != scan.Input)
        {
            throw new InvalidOperationException("Font files or analysis inputs changed during analysis; scan again.");
        }

        progress?.Report(100);
    }

    private async Task<(IReadOnlyList<FontFileRecord> Records, int ReusedCount)> ObserveAsync(
        FontDirectoryPolicyResolver policy, IReadOnlyList<FontFileRecord> previous, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(0);
        foreach (FontDirectoryRoot root in policy.GetConfiguredRoots())
        {
            if (!Directory.Exists(root.Path) && ((root.Policy & FontDirectoryPolicy.Custom) != 0 || previous.Any(file => FontPathComparison.IsWithin(file.Path, root.Path))))
            {
                throw new DirectoryNotFoundException("Configured font directory is unavailable: " + root.Path);
            }
        }

        string[] paths = EnumerateFiles(policy, cancellationToken).Distinct(FontPathComparison.Comparer).OrderBy(path => path, FontPathComparison.Comparer).ToArray();
        progress?.Report(2);
        Dictionary<string, FontFileRecord> reusable = previous.GroupBy(file => file.PhysicalIdentity, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        ObservedFile[] observedFiles = GroupPhysicalFiles(paths, policy);
        var records = new List<FontFileRecord>(observedFiles.Length);
        int reusedCount = 0;
        progress?.Report(5);
        foreach (ObservedFile observed in observedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(observed.Path);
            long size = info.Length;
            DateTime modified = info.LastWriteTimeUtc;
            byte[] data = await File.ReadAllBytesAsync(observed.Path, cancellationToken).ConfigureAwait(false);
            byte[] hash = SHA256.HashData(data);

            info.Refresh();
            if (!info.Exists || info.Length != size || info.LastWriteTimeUtc != modified || _identityProvider.GetIdentity(observed.Path) != observed.PhysicalIdentity)
            {
                throw new IOException("Font changed while being scanned: " + observed.Path);
            }

            FontFileRecord record;
            if (reusable.TryGetValue(observed.PhysicalIdentity, out FontFileRecord? existing)
                && existing.Health == FontFileHealth.Healthy && existing.Faces.Count > 0
                && existing.Faces.All(face => face.NameRecords.Count > 0 && face.UnicodeCoverage.Count > 0)
                && existing.Size == size && existing.ModifiedUtc == new DateTimeOffset(modified)
                && existing.Sha256.Span.SequenceEqual(hash))
            {
                record = CloneForGeneration(existing, observed, 0);
                reusedCount++;
            }
            else
            {
                record = await InspectFileAsync(observed.Path, observed.PhysicalIdentity, observed.LinkKind, 0, policy, data, cancellationToken).ConfigureAwait(false);
                if (record.Health == FontFileHealth.Healthy && !record.Sha256.Span.SequenceEqual(hash))
                {
                    throw new IOException("Font changed while being inspected: " + observed.Path);
                }
            }

            records.Add(record with { Sha256 = hash, PathAliases = observed.Aliases, DirectoryPolicy = observed.Policy });
            progress?.Report(5 + ((records.Count * 85d) / Math.Max(1, observedFiles.Length)));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return (records, reusedCount);
    }

    /// <summary>Releases the scanner serialization gate.</summary>
    public void Dispose() => _scanGate.Dispose();

    private static IEnumerable<string> EnumerateFiles(FontDirectoryPolicyResolver policy, CancellationToken cancellationToken)
    {
        foreach (FontDirectoryRoot root in policy.GetScanRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string path in Directory.EnumerateFiles(root.Path, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (FontDirectoryPolicyResolver.IsSupportedFont(path))
                {
                    yield return Path.GetFullPath(path);
                }
            }
        }
    }

    private Task<FontFileRecord> InspectFileAsync(
        string path,
        string physicalIdentity,
        FontLinkKind linkKind,
        long generationId,
        FontDirectoryPolicyResolver policy,
        byte[] data,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenTypeContainer container = OpenTypeFontReader.Read(data);
            var faces = new List<FontFaceRecord>(container.Faces.Count);
            foreach (OpenTypeFace face in container.Faces)
            {
                InspectedFontFace inspected = FontFaceInspector.Inspect(path, face);
                if (!inspected.IsClassifiable)
                {
                    return Task.FromResult(CreateUnclassifiable(path, physicalIdentity, generationId, info, policy, linkKind, inspected.Error ?? "Font Face could not be classified."));
                }

                FontFaceFingerprints fingerprints = FontFingerprintBuilder.BuildFace(face);
                faces.Add(new FontFaceRecord
                {
                    FaceIndex = face.FaceIndex,
                    StrictFingerprint = fingerprints.StrictFingerprint,
                    FamilyName = inspected.FamilyName,
                    StyleName = inspected.StyleName,
                    Names = inspected.Names,
                    NameRecords = inspected.NameRecords,
                    UnicodeCoverage = inspected.UnicodeCoverage,
                    Weight = inspected.Weight,
                    Width = inspected.Width,
                    Slant = inspected.Slant,
                    IsItalic = inspected.IsItalic,
                    IsFixedPitch = inspected.IsFixedPitch,
                    Technology = inspected.Technology,
                    UnitsPerEm = inspected.UnitsPerEm,
                    Version = inspected.Version,
                    IsRuntimePreferred = (policy.GetPolicy(path) & FontDirectoryPolicy.Preferred) != 0
                });
            }

            return Task.FromResult(new FontFileRecord
            {
                GenerationId = generationId,
                Path = path,
                NormalizedPath = NormalizePath(path),
                PhysicalIdentity = physicalIdentity,
                Size = info.Length,
                ModifiedUtc = info.LastWriteTimeUtc,
                Sha256 = SHA256.HashData(data),
                DirectoryPolicy = policy.GetPolicy(path),
                LinkKind = linkKind,
                Health = FontFileHealth.Healthy,
                Faces = faces
            });
        }
        catch (Exception exception) when (exception is InvalidDataException)
        {
            _logger.LogWarning("Font inventory refused {Path}: {Reason}", path, exception.Message);
            return Task.FromResult(CreateUnclassifiable(path, physicalIdentity, generationId, info, policy, linkKind, exception.Message));
        }
    }

    private FontFileRecord CreateUnclassifiable(
        string path,
        string physicalIdentity,
        long generationId,
        FileInfo info,
        FontDirectoryPolicyResolver policy,
        FontLinkKind linkKind,
        string error)
    {
        return new FontFileRecord
        {
            GenerationId = generationId,
            Path = path,
            NormalizedPath = NormalizePath(path),
            PhysicalIdentity = physicalIdentity,
            Size = info.Exists ? info.Length : 0,
            ModifiedUtc = info.Exists ? info.LastWriteTimeUtc : DateTimeOffset.MinValue,
            Sha256 = ReadOnlyMemory<byte>.Empty,
            DirectoryPolicy = policy.GetPolicy(path),
            LinkKind = linkKind,
            Health = FontFileHealth.Unclassifiable,
            Error = error,
            Faces = []
        };
    }

    private ObservedFile[] GroupPhysicalFiles(IEnumerable<string> paths, FontDirectoryPolicyResolver policy)
    {
        return paths
            .Select(path =>
            {
                var info = new FileInfo(path);
                FontLinkKind linkKind = info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0
                    ? FontLinkKind.SymbolicOrReparsePoint
                    : FontLinkKind.None;
                string physicalIdentity = _identityProvider.GetIdentity(path);
                string groupingIdentity = linkKind == FontLinkKind.SymbolicOrReparsePoint ? physicalIdentity + "\0" + path : physicalIdentity;
                return new ObservedPath(path, groupingIdentity, physicalIdentity, linkKind, policy.GetPolicy(path));
            })
            .GroupBy(observed => observed.GroupingIdentity, StringComparer.Ordinal)
            .Select(group =>
            {
                ObservedPath[] members = group.ToArray();
                ObservedPath canonical = members
                    .OrderByDescending(member => (member.Policy & FontDirectoryPolicy.Preferred) != 0)
                    .ThenBy(member => member.Path, FontPathComparison.Comparer)
                    .First();
                FontDirectoryPolicy combinedPolicy = members.Aggregate(FontDirectoryPolicy.None, (current, member) => current | member.Policy);
                string[] aliases = members.Where(member => member.Path != canonical.Path).Select(member => member.Path).OrderBy(path => path, FontPathComparison.Comparer).ToArray();
                return new ObservedFile(canonical.Path, canonical.PhysicalIdentity, canonical.LinkKind, combinedPolicy, aliases);
            })
            .OrderBy(file => file.Path, FontPathComparison.Comparer)
            .ToArray();
    }

    private static FontFileRecord CloneForGeneration(
        FontFileRecord source,
        ObservedFile observed,
        long generationId)
    {
        return source with
        {
            Id = 0,
            GenerationId = generationId,
            Path = observed.Path,
            NormalizedPath = NormalizePath(observed.Path),
            PhysicalIdentity = observed.PhysicalIdentity,
            PathAliases = observed.Aliases,
            DirectoryPolicy = observed.Policy,
            LinkKind = observed.LinkKind,
            Faces = source.Faces.Select(face => face with { Id = 0 }).ToArray()
        };
    }

    private static string NormalizePath(string path) => OperatingSystem.IsWindows() ? Path.GetFullPath(path).ToUpperInvariant() : Path.GetFullPath(path);

    private sealed record ObservedPath(
        string Path,
        string GroupingIdentity,
        string PhysicalIdentity,
        FontLinkKind LinkKind,
        FontDirectoryPolicy Policy);

    private sealed record ObservedFile(
        string Path,
        string PhysicalIdentity,
        FontLinkKind LinkKind,
        FontDirectoryPolicy Policy,
        IReadOnlyList<string> Aliases);
}
