using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;
using Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;
using Jellyfin.Plugin.AssSubsetter.Models;

namespace Jellyfin.Plugin.AssSubsetter.Services;

/// <summary>
/// Owns the published in-memory font lookup snapshot and read leases used by subtitle processing.
/// </summary>
public sealed class FontCacheManager : IDisposable
{
    private readonly IFontRepository _repository;
    private readonly FontInventoryScanner _scanner;
    private readonly FontMaintenanceCoordinator _coordinator;
    private readonly Func<RuntimeFontFace, bool> _canSelect;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly SubtitleFontRequestCache _subtitleRequests = new();
    private readonly AsyncLocal<SubtitleFontDependencyValidation?> _dependencyValidation = new();
    private RuntimeFontIndex _runtimeIndex = RuntimeFontIndex.Empty;

    /// <summary>Initializes a new instance of the <see cref="FontCacheManager"/> class backed by SQLite.</summary>
    /// <param name="repository">The durable font catalog.</param>
    /// <param name="scanner">The staged inventory scanner.</param>
    /// <param name="coordinator">The file read/maintenance coordinator.</param>
    public FontCacheManager(
        IFontRepository repository,
        FontInventoryScanner scanner,
        FontMaintenanceCoordinator coordinator)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _canSelect = face => _coordinator.IsPathAvailable(face.Path);
    }

    internal SubtitleFontDependencies GetSubtitleDependencies(string sourcePath)
    {
        var requests = _subtitleRequests.Get(sourcePath);
        RuntimeFontIndex snapshot = Volatile.Read(ref _runtimeIndex);
        var selections = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var request in requests)
        {
            RuntimeFontFace? face = snapshot.Find(request.Key, _canSelect, request.Value);
            string identity = "missing";
            if (face is not null)
            {
                string hash = face.ContentHash;
                if (string.IsNullOrEmpty(hash))
                {
                    // Production SQLite snapshots always project SHA-256. An incomplete
                    // custom snapshot must fail byte validation rather than read without a lease.
                    hash = "unpublished";
                }

                identity = SubtitleFontDependencies.FaceIdentity(hash, face.FaceIndex);
            }

            selections[SubtitleFontDependencies.RequestKey(request.Key, request.Value)] = identity;
        }

        return new SubtitleFontDependencies(selections);
    }

    internal SubtitleFontDependencyValidation BeginDependencyValidation(SubtitleFontDependencies dependencies)
    {
        SubtitleFontDependencyValidation? previous = _dependencyValidation.Value;
        var validation = new SubtitleFontDependencyValidation(dependencies, () => _dependencyValidation.Value = previous);
        _dependencyValidation.Value = validation;
        return validation;
    }

    internal void ValidateFontDependency(FontDescriptor descriptor, IReadOnlyCollection<uint> characters, FontFileLease? lease, byte[]? bytes)
        => _dependencyValidation.Value?.Observe(descriptor, characters, lease, bytes);

    /// <summary>Releases synchronization resources owned by the manager.</summary>
    public void Dispose() => _loadGate.Dispose();

    /// <summary>Restores durable path fences within the same boundary as startup snapshot publication.</summary>
    /// <param name="restoreFencesAsync">Restores maintenance fences before the snapshot becomes visible.</param>
    /// <param name="cancellationToken">A token that cancels repository access.</param>
    /// <returns>A task representing guarded snapshot publication.</returns>
    public async Task LoadPublishedSnapshotAsync(Func<CancellationToken, Task>? restoreFencesAsync, CancellationToken cancellationToken)
    {
        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _repository.InitializeAsync(cancellationToken).ConfigureAwait(false);
            if (restoreFencesAsync is not null)
            {
                await restoreFencesAsync(cancellationToken).ConfigureAwait(false);
            }

            IReadOnlyList<RuntimeFontFace> faces = await _repository.LoadPublishedRuntimeFacesAsync(cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _runtimeIndex, new RuntimeFontIndex(faces));
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>Stages an inventory generation without changing the published runtime snapshot.</summary>
    /// <param name="progress">Receives percentage completion.</param>
    /// <param name="forceRebuild">Forces rebuilding derived data.</param>
    /// <param name="cancellationToken">Cancels the scan and retains the previous snapshot.</param>
    /// <returns>The staged generation identifier.</returns>
    public Task<FontInventoryScanResult> StageInventoryAsync(IProgress<double>? progress = null, bool forceRebuild = false, CancellationToken cancellationToken = default)
        => _scanner.StageAsync(progress, forceRebuild, cancellationToken);

    /// <summary>Validates the captured inputs before publication.</summary>
    /// <param name="scan">The completed scan.</param>
    /// <param name="cancellationToken">Cancels validation.</param>
    /// <param name="progress">Optional progress during final input verification.</param>
    /// <returns>A task representing validation.</returns>
    public Task ValidatePublicationInputsAsync(FontInventoryScanResult scan, CancellationToken cancellationToken, IProgress<double>? progress = null)
        => _scanner.ValidatePublicationInputsAsync(scan, cancellationToken, progress);

    /// <summary>Publishes a completed analysis generation and its prepared runtime index under the startup-load boundary.</summary>
    /// <param name="batch">The complete unpersisted analysis.</param>
    /// <param name="cancellationToken">Cancels before durable publication.</param>
    /// <returns>A task representing publication.</returns>
    /// <param name="preparedCandidates">The candidate result prepared before final input validation.</param>
    public async Task<AnalysisBatch> PublishAnalyzedGenerationAsync(AnalysisBatch batch, CancellationToken cancellationToken, FontRenameCandidatePreparation? preparedCandidates = null)
    {
        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (batch.Status != AnalysisBatchStatus.Complete || batch.Id != 0)
            {
                throw new InvalidOperationException("Publication requires a completed unpersisted analysis.");
            }

            IReadOnlyList<RuntimeFontFace> faces = await _repository.LoadRuntimeFacesAsync(batch.GenerationId, cancellationToken).ConfigureAwait(false);
            Dictionary<long, FontFaceAnalysisRecord> scores = batch.FaceScores.ToDictionary(face => face.FaceId);
            var prepared = new RuntimeFontIndex(faces.Select(face => scores.TryGetValue(face.FaceId, out FontFaceAnalysisRecord? score)
                ? face with { RetentionScore = score.RetentionScore, IsRuntimePreferred = score.IsRuntimePreferred }
                : face).ToArray());
            cancellationToken.ThrowIfCancellationRequested();
            AnalysisBatch published = await _repository.PublishAnalysisAsync(batch, cancellationToken, preparedCandidates).ConfigureAwait(false);
            Volatile.Write(ref _runtimeIndex, prepared);
            _coordinator.ClearRenameFences();
            return published;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>Finds a Face that can cover the subtitle characters in the current memory snapshot.</summary>
    /// <param name="descriptor">The requested ASS font attributes.</param>
    /// <param name="requiredCharacters">Unicode scalar values used by the subtitle for this font.</param>
    /// <param name="cancellationToken">Cancels waiting for a stable file lease.</param>
    /// <returns>A stable file lease, or <see langword="null"/> when the snapshot is cold or no Face matches.</returns>
    public async Task<FontFileLease?> AcquireFontAsync(FontDescriptor descriptor, IReadOnlyCollection<uint> requiredCharacters, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (true)
        {
            RuntimeFontIndex snapshot = Volatile.Read(ref _runtimeIndex);
            RuntimeFontFace? face = snapshot.Find(descriptor, _canSelect, requiredCharacters);
            if (face is null)
            {
                return null;
            }

            FontFileLease lease = await _coordinator.AcquireReadAsync(face, cancellationToken).ConfigureAwait(false);
            if (_canSelect(face) && ReferenceEquals(snapshot, Volatile.Read(ref _runtimeIndex)))
            {
                return lease;
            }

            await lease.DisposeAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
