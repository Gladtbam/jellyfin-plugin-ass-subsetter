using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;
using Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Api;

/// <summary>Maps one consistent database status page without calculating or accessing files.</summary>
public sealed class FontRenameStateQuery
{
    private readonly IFontRepository _repository;
    private readonly FontRenameCandidateService _candidates;

    /// <summary>Initializes a new instance of the <see cref="FontRenameStateQuery"/> class.</summary>
    /// <param name="repository">The transactional database page boundary.</param>
    /// <param name="candidates">The lightweight current input requirements.</param>
    public FontRenameStateQuery(IFontRepository repository, FontRenameCandidateService candidates)
    {
        _repository = repository;
        _candidates = candidates;
    }

    /// <summary>Reads a filtered, bounded page and observable generation state.</summary>
    /// <param name="status">pending, skipped, failed, renamed or rolledback.</param>
    /// <param name="page">The one-based page.</param>
    /// <param name="pageSize">The page size, bounded to 1–200.</param>
    /// <param name="cancellationToken">Cancels the database read.</param>
    /// <returns>The consistent page, count and authorization availability.</returns>
    public async Task<RenameStatePageDto> GetPageAsync(string status, int page, int pageSize, CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var requirements = _candidates.ReadRequirements();
        var result = await _repository.LoadRenameStatePageAsync(status, page, pageSize, requirements, cancellationToken).ConfigureAwait(false);
        bool current = result.Context.IsCurrent && requirements == _candidates.ReadRequirements();
        var state = result.Context.State;
        var context = current && state is not null ? new FontRenameContextDto(state.GenerationId, state.AnalysisBatchId, state.Rules, state.RuleFingerprint) : null;
        var generation = state is null ? null : new FontRenameCandidateStateDto(
            (!current && state.Status == FontRenameCandidateGenerationStatus.Ready ? FontRenameCandidateGenerationStatus.Stale : state.Status).ToString().ToLowerInvariant(),
            state.GeneratedUtc,
            state.OperationRequestId,
            state.Error);
        var rows = result.Items.Select(row => new RenameStateItemDto(row.Key, row.MaintenanceBatchId, row.OperationRequestId, row.GenerationId, row.FileId, row.CanExecute && current, row.CanRollback, ToDto(row.Item))).ToArray();
        var publication = result.Context.Publication;
        var refreshRequirements = _candidates.ReadRequirements();
        bool canRefresh = publication is not null
            && publication.EvidenceVersion == refreshRequirements.AnalysisEvidenceVersion
            && publication.Input.PolicyFingerprint == refreshRequirements.PolicyFingerprint
            && publication.Input.EvidenceFingerprint == refreshRequirements.EvidenceFingerprint;
        return new(context, current ? null : result.Context.BlockReason ?? "Rename candidate inputs changed.", rows, page, pageSize, result.TotalCount, generation, canRefresh);
    }

    internal static RenameItemDto ToDto(RenameItemRecord item)
        => new(item.FileId, item.OldPath, item.NewPath, Convert.ToHexString(item.Evidence.Sha256.Span), item.SourceRoot, item.NamingEvidence, item.CollisionFingerprint, item.Status, item.Error) { InspectionError = item.Evidence.Error };
}
