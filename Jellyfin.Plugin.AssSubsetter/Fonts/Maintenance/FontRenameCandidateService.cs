using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance.Naming;
using Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

/// <summary>Prepares persisted candidates and authorizes exact selected evidence.</summary>
public sealed class FontRenameCandidateService
{
    private readonly IFontRepository _repository;
    private readonly FontRenamePlanner _planner;
    private readonly Func<FontNamingRules> _rules;
    private readonly Func<FontDirectoryPolicyResolver> _policy;
    private readonly TimeProvider _time;
    private readonly IQuarantineFileSystem _files;
    private readonly IFontRenameDirectoryOperations _directories;
    private readonly FontMaintenanceCoordinator _coordinator;

    /// <summary>Initializes a new instance of the <see cref="FontRenameCandidateService"/> class.</summary>
    /// <param name="repository">Publication, candidates and durable requests.</param>
    /// <param name="planner">The background candidate calculator.</param>
    /// <param name="rules">Current naming configuration.</param>
    /// <param name="policy">Current directory configuration.</param>
    /// <param name="time">The generation and request clock.</param>
    /// <param name="files">Fresh selected file evidence.</param>
    /// <param name="directories">Uncached selected directory checks.</param>
    /// <param name="coordinator">Shared runtime file leases.</param>
    public FontRenameCandidateService(IFontRepository repository, FontRenamePlanner planner, Func<FontNamingRules> rules, Func<FontDirectoryPolicyResolver> policy, TimeProvider time, IQuarantineFileSystem files, IFontRenameDirectoryOperations directories, FontMaintenanceCoordinator coordinator)
    {
        _repository = repository;
        _planner = planner;
        _rules = rules;
        _policy = policy;
        _time = time;
        _files = files;
        _directories = directories;
        _coordinator = coordinator;
    }

    /// <summary>Captures compatible analysis and naming inputs without reading font data.</summary>
    /// <returns>The current exact input requirements.</returns>
    public FontRenameCandidateReadRequirements ReadRequirements()
    {
        var input = FontInventoryInputComparer.Capture([], _policy(), PhysicalFontAnalysisEvidenceSource.AnalysisCompatibilityKey);
        return new(_rules().GetFingerprint(), input.PolicyFingerprint, input.EvidenceFingerprint, FontDuplicateAnalyzer.CurrentEvidenceVersion, FontRenamePlanner.CurrentCandidateVersion);
    }

    /// <summary>Prepares candidates from staged inventory and the new analysis before publication.</summary>
    /// <param name="analysis">The completed analysis whose persisted ID is not yet allocated.</param>
    /// <param name="cancellationToken">Cancels preparation.</param>
    /// <returns>The complete result or an independent candidate generation failure.</returns>
    public Task<FontRenameCandidatePreparation> PrepareForPublicationAsync(AnalysisBatch analysis, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(analysis.Id, 0);
        return PrepareAsync(analysis, null, cancellationToken);
    }

    /// <summary>Recalculates candidates for one accepted background request.</summary>
    /// <param name="requestId">The calculation root request.</param>
    /// <param name="cancellationToken">Cancels preparation and publication.</param>
    /// <returns>The committed generation state.</returns>
    public async Task<FontRenameCandidateState> RebuildCurrentAsync(long requestId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestId);
        var requirements = ReadRequirements();
        var context = await _repository.GetRenameCandidateContextAsync(requirements, cancellationToken).ConfigureAwait(false);
        var publication = context.Publication ?? throw new InvalidOperationException("A complete current font analysis is required.");
        var rules = _rules();
        var state = new FontRenameCandidateState(publication.GenerationId, publication.AnalysisBatchId, rules, rules.GetFingerprint(), FontRenamePlanner.CurrentCandidateVersion, FontRenameCandidateGenerationStatus.Pending, context.State?.GeneratedUtc, requestId, null);
        if (context.State?.OperationRequestId != requestId)
        {
            await _repository.SetRenameCandidateStateAsync(state, requirements, cancellationToken).ConfigureAwait(false);
        }

        await _repository.SetRenameCandidateStateAsync(state with { Status = FontRenameCandidateGenerationStatus.Running }, requirements, cancellationToken).ConfigureAwait(false);
        var analysis = await _repository.GetAnalysisBatchAsync(publication.AnalysisBatchId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The source analysis is no longer available.");
        var prepared = await PrepareAsync(analysis, requestId, cancellationToken).ConfigureAwait(false);
        if (ReadRequirements() != requirements || prepared.State.RuleFingerprint != requirements.RuleFingerprint)
        {
            throw new InvalidOperationException("The rename candidate inputs changed during calculation.");
        }

        await _repository.SaveRenameCandidatesAsync(prepared, requirements, requestId, cancellationToken).ConfigureAwait(false);
        return prepared.State;
    }

    /// <summary>Reads the complete persisted current set for background consumers without calculating.</summary>
    /// <param name="cancellationToken">Cancels the database read.</param>
    /// <returns>The current persisted snapshot.</returns>
    public async Task<FontRenameSnapshot> GetCurrentAsync(CancellationToken cancellationToken)
    {
        var requirements = ReadRequirements();
        var selected = await _repository.LoadRenameCandidatesAsync(null, requirements, cancellationToken).ConfigureAwait(false);
        if (!selected.Context.IsCurrent || ReadRequirements() != requirements || FontNamingTemplate.ValidateRules(_rules()).Count != 0)
        {
            throw new InvalidOperationException(selected.Context.BlockReason ?? "The rename candidate inputs changed.");
        }

        var state = selected.Context.State!;
        return new(state.GenerationId, state.AnalysisBatchId, state.Rules, state.RuleFingerprint, selected.Items);
    }

    /// <summary>Checks immutable authorization independently of candidate recalculation status.</summary>
    /// <param name="payload">The saved immutable authorization.</param>
    /// <param name="cancellationToken">Cancels metadata reads.</param>
    /// <returns>Whether analysis and configuration still match the authorization.</returns>
    public async Task<bool> IsCurrentAsync(FontRenameExecutionPayload payload, CancellationToken cancellationToken)
    {
        var requirements = ReadRequirements();
        if (payload.Rules is null || payload.RuleFingerprint != payload.Rules.GetFingerprint()
            || payload.RuleFingerprint != requirements.RuleFingerprint || FontNamingTemplate.ValidateRules(payload.Rules).Count != 0)
        {
            return false;
        }

        var context = await _repository.GetRenameCandidateContextAsync(requirements, cancellationToken).ConfigureAwait(false);
        var publication = context.Publication;
        return publication is not null && publication.GenerationId == payload.GenerationId && publication.AnalysisBatchId == payload.AnalysisBatchId
            && publication.EvidenceVersion == requirements.AnalysisEvidenceVersion
            && publication.Input.PolicyFingerprint == requirements.PolicyFingerprint && publication.Input.EvidenceFingerprint == requirements.EvidenceFingerprint
            && ReadRequirements() == requirements;
    }

    /// <summary>Authorizes selected IDs from persisted candidates with fresh safety checks.</summary>
    /// <param name="generationId">The displayed generation.</param>
    /// <param name="analysisBatchId">The displayed analysis.</param>
    /// <param name="ruleFingerprint">The displayed rules.</param>
    /// <param name="selectedFileIds">The exact selected file IDs.</param>
    /// <param name="idempotencyKey">The immutable retry key.</param>
    /// <param name="cancellationToken">Cancels reads and persistence.</param>
    /// <returns>The durable immutable authorization.</returns>
    public async Task<OperationRequest> CreateRequestAsync(long generationId, long analysisBatchId, string ruleFingerprint, IReadOnlyList<long> selectedFileIds, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selectedFileIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        long[] ids = selectedFileIds.Distinct().Order().ToArray();
        if (ids.Length == 0 || ids.Any(id => id <= 0))
        {
            throw new ArgumentException("Select at least one valid physical file.", nameof(selectedFileIds));
        }

        var existing = await _repository.GetOperationRequestByIdempotencyKeyAsync(idempotencyKey.Trim(), cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.Kind != FontOperationKind.NormalizeNames)
            {
                throw new InvalidOperationException("The retry key identifies another operation.");
            }

            var saved = FontOperationRequestPayloadCodec.ParseRenameExecution(existing.RequestJson);
            if (saved.GenerationId != generationId || saved.AnalysisBatchId != analysisBatchId || saved.RuleFingerprint != ruleFingerprint || !saved.Items.Select(item => item.FileId).Order().SequenceEqual(ids))
            {
                throw new InvalidOperationException("The retry key identifies different immutable authorization.");
            }

            return existing;
        }

        var requirements = ReadRequirements();
        var selected = await _repository.LoadRenameCandidatesAsync(ids, requirements, cancellationToken).ConfigureAwait(false);
        var state = selected.Context.State;
        if (!selected.Context.IsCurrent || state is null || state.GenerationId != generationId || state.AnalysisBatchId != analysisBatchId || state.RuleFingerprint != ruleFingerprint)
        {
            throw new InvalidOperationException("The displayed filename candidates are stale or not ready.");
        }

        if (selected.Items.Count != ids.Length)
        {
            throw new ArgumentException("Unknown selected file.", nameof(selectedFileIds));
        }

        foreach (var candidate in selected.Items)
        {
            string? reason = candidate.SkipReason ?? await GetLiveExclusionAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (reason is not null)
            {
                throw new InvalidOperationException("A selected file is no longer eligible: " + reason);
            }
        }

        var items = selected.Items.Select(ToItem).ToArray();
        var payload = new FontRenameExecutionPayload(generationId, analysisBatchId, state.Rules, ruleFingerprint, items);
        var after = await _repository.GetRenameCandidateContextAsync(ReadRequirements(), cancellationToken).ConfigureAwait(false);
        if (!after.IsCurrent || after.State != state || ReadRequirements() != requirements || !await IsCurrentAsync(payload, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The authorization context changed.");
        }

        return await new FontOperationRequestStore(_repository, _time).CreateRenameExecutionAsync(payload, idempotencyKey, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Authorizes fresh eligible persisted work for a scheduled invocation.</summary>
    /// <param name="cancellationToken">Cancels reads and persistence.</param>
    /// <returns>The accepted request or null when no safe candidate remains.</returns>
    public async Task<OperationRequest?> CreateScheduledRequestAsync(CancellationToken cancellationToken)
    {
        var current = await GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var active = await _repository.LoadActiveRenameRequestsAsync(cancellationToken).ConfigureAwait(false);
        var reserved = active.SelectMany(request => FontOperationRequestPayloadCodec.ParseRenameExecution(request.RequestJson).Items).ToArray();
        var eligible = new List<FontRenameCandidate>();
        foreach (var candidate in current.Items.Where(item => item.SkipReason is null))
        {
            if (!reserved.Any(saved => FontPathComparison.Comparer.Equals(saved.OldPath, candidate.File.Path) || FontPathComparison.Comparer.Equals(saved.NewPath, candidate.NewPath))
                && await GetLiveExclusionAsync(candidate, cancellationToken).ConfigureAwait(false) is null)
            {
                eligible.Add(candidate);
            }
        }

        if (eligible.Count == 0)
        {
            return null;
        }

        string identity = current.GenerationId + ":" + current.AnalysisBatchId + ":" + current.RuleFingerprint + ":" + string.Join("|", eligible.OrderBy(item => item.File.Id).Select(item => item.File.Id + ":" + Convert.ToHexString(item.File.Sha256.Span) + ":" + item.NewPath));
        string baseKey = "rename-current:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        var history = await _repository.LoadRenameStateRequestsAsync(cancellationToken).ConfigureAwait(false);
        long lastAttempt = history.Where(request => request.Status is OperationRequestStatus.Complete or OperationRequestStatus.Failed or OperationRequestStatus.Cancelled)
            .Where(request => request.IdempotencyKey == baseKey || request.IdempotencyKey.StartsWith(baseKey + ":attempt:", StringComparison.Ordinal))
            .Select(request => request.Id).DefaultIfEmpty(0).Max();
        string key = lastAttempt == 0 ? baseKey : baseKey + ":attempt:" + lastAttempt;
        return await CreateRequestAsync(current.GenerationId, current.AnalysisBatchId, current.RuleFingerprint, eligible.Select(item => item.File.Id).ToArray(), key, cancellationToken).ConfigureAwait(false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Independent planner failures are reported as candidate generation failures; cancellation and all repository failures remain outside this boundary.")]
    private async Task<FontRenameCandidatePreparation> PrepareAsync(AnalysisBatch analysis, long? requestId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FontNamingRules rules = _rules();
        var requirements = ReadRequirements();
        var files = await _repository.LoadInventoryAsync(analysis.GenerationId, cancellationToken).ConfigureAwait(false);
        if (files.Any(file => file.GenerationId != analysis.GenerationId))
        {
            throw new InvalidOperationException("The candidate inventory generation changed.");
        }

        var state = new FontRenameCandidateState(analysis.GenerationId, analysis.Id, rules, rules.GetFingerprint(), FontRenamePlanner.CurrentCandidateVersion, FontRenameCandidateGenerationStatus.Ready, _time.GetUtcNow(), requestId, null);
        IReadOnlyList<FontRenameCandidate> items;
        try
        {
            if (FontNamingTemplate.ValidateRules(rules).Count != 0)
            {
                throw new ArgumentException("Invalid font naming rules.");
            }

            var excluded = analysis.Groups.SelectMany(group => group.Members).Where(member => member.Decision == DuplicateGroupMemberDecision.QuarantineCandidate).Select(member => member.FileId).ToHashSet();
            items = _planner.Build(analysis.GenerationId, analysis.Id, files, excluded, rules, _policy(), cancellationToken).Items;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            state = state with { Status = FontRenameCandidateGenerationStatus.Failed, Error = exception.Message };
            items = [];
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (ReadRequirements() != requirements || rules.GetFingerprint() != requirements.RuleFingerprint)
        {
            state = state with { Status = FontRenameCandidateGenerationStatus.Stale, Error = "Rename candidate inputs changed during calculation." };
            items = [];
        }

        return new(state, items);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA3003:File path injection", Justification = "Paths come exclusively from persisted server-generated candidates selected by numeric IDs; both paths are validated against the physical configured source root.")]
    private async Task<string?> GetLiveExclusionAsync(FontRenameCandidate candidate, CancellationToken cancellationToken)
    {
        try
        {
            string path = candidate.File.Path;
            string? target = candidate.NewPath;
            string? root = candidate.SourceRoot;
            var policy = _policy();
            if (root is null || target is null || !FontPathComparison.IsWithin(path, root) || !FontPathComparison.IsWithin(target, root)
                || !FontPathComparison.Comparer.Equals(policy.GetOwningCustomRoot(path), root) || !FontPathComparison.Comparer.Equals(policy.GetOwningCustomRoot(target), root)
                || (policy.GetPolicy(path) & FontDirectoryPolicy.Protected) != 0 || (policy.GetPolicy(target) & FontDirectoryPolicy.Protected) != 0)
            {
                return "source-root-changed-or-protected";
            }

            if (!_files.IsPhysicalPath(path) || !_files.IsPhysicalPath(target))
            {
                return "linked-path";
            }

            var probe = _directories.Probe(path, root, Path.GetDirectoryName(target)!);
            if (!probe.IsSafe || !probe.IsSameFileSystem)
            {
                return probe.Error ?? "different-filesystem";
            }

            if (_coordinator.IsInUse(path) || _coordinator.IsInUse(target))
            {
                return "in-use";
            }

            var evidence = await _files.ReadEvidenceAsync(path, cancellationToken).ConfigureAwait(false);
            if (evidence is null || evidence.Size != candidate.File.Size || evidence.ModifiedUtc != candidate.File.ModifiedUtc || evidence.LinkCount != 1
                || evidence.PhysicalIdentity != candidate.File.PhysicalIdentity || !evidence.Sha256.Span.SequenceEqual(candidate.File.Sha256.Span))
            {
                return "source-evidence-drift";
            }

            if (!FontPathComparison.Comparer.Equals(path, target) && (_files.FileExists(target) || Directory.Exists(target)))
            {
                return "target-occupied";
            }

            return !_files.IsPhysicalPath(path) || !_files.IsPhysicalPath(target) ? "linked-path" : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "unsafe-file: " + exception.Message;
        }
    }

    internal static RenameItemRecord ToItem(FontRenameCandidate candidate)
        => new() { FileId = candidate.File.Id, Evidence = candidate.File, OldPath = candidate.File.Path, NewPath = candidate.NewPath ?? string.Empty, SourceRoot = candidate.SourceRoot ?? string.Empty, NamingEvidence = candidate.NamingEvidence, CollisionFingerprint = candidate.CollisionFingerprint, Status = candidate.SkipReason is null ? RenameItemStatus.Pending : RenameItemStatus.Skipped, Error = candidate.SkipReason };
}
