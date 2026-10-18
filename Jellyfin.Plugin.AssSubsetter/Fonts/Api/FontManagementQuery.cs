using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Api;

/// <summary>Builds safe administrator read models exclusively from persisted font repository data.</summary>
public sealed class FontManagementQuery
{
    private const int MaximumPageSize = 200;
    private static readonly string[] Technologies = ["TrueType", "OpenType", "OpenType CFF", "OpenType CFF2"];
    private readonly IFontRepository _repository;
    private readonly Func<FontDirectoryPolicyResolver>? _policy;

    /// <summary>Initializes a new instance of the <see cref="FontManagementQuery"/> class.</summary>
    /// <param name="repository">The durable font repository used by all API reads.</param>
    /// <param name="policy">Current configured analysis inputs.</param>
    public FontManagementQuery(IFontRepository repository, Func<FontDirectoryPolicyResolver>? policy = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _policy = policy;
    }

    private enum UnclassifiableFilter
    {
        All,
        Only,
        Exclude
    }

    /// <summary>Loads the persisted inventory and latest completed analysis overview.</summary>
    /// <param name="cancellationToken">A token that cancels the repository reads.</param>
    /// <returns>The administrator overview.</returns>
    public async Task<FontOverviewDto> GetOverviewAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<FontFileRecord> files = await _repository.LoadPublishedInventoryAsync(cancellationToken).ConfigureAwait(false);
        AnalysisBatch? latest = await _repository.GetLatestCompleteAnalysisAsync(cancellationToken).ConfigureAwait(false);
        FontDatabaseHealth health = await _repository.GetDatabaseHealthAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<PerformanceSample> samples = await _repository.LoadRecentPerformanceSamplesAsync(50, cancellationToken).ConfigureAwait(false);
        AnalysisBatchDto? latestState = latest is null ? null : await GetAnalysisStateAsync(latest, cancellationToken).ConfigureAwait(false);
        AnalysisBatch? currentAnalysis = latestState?.IsCurrent == true ? latest : null;
        long eligibleQuarantineCount = currentAnalysis?.Groups
            .SelectMany(group => group.Members)
            .Where(member => member.Decision == DuplicateGroupMemberDecision.QuarantineCandidate)
            .Select(member => member.FileId)
            .Distinct()
            .LongCount() ?? 0;
        return new FontOverviewDto(
            health.PublishedGenerationId,
            files.Count,
            files.Sum(file => file.Faces.Count),
            latestState,
            eligibleQuarantineCount,
            currentAnalysis?.ReclaimableBytes ?? 0,
            ToDto(health),
            samples.Select(ToDto).ToArray())
        {
            LastScan = ReadScanResult(await _repository.GetLatestCompletedAnalysisOperationAsync(cancellationToken).ConfigureAwait(false))
        };
    }

    /// <summary>Loads safe pageable immutable analysis reports.</summary>
    /// <param name="page">The requested one-based page.</param>
    /// <param name="pageSize">The requested page size.</param>
    /// <param name="cancellationToken">A token that cancels the repository read.</param>
    /// <returns>The bounded analysis-report page.</returns>
    public async Task<PagedResult<AnalysisBatchDto>> GetAnalysisBatchesAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        (int normalizedPage, int normalizedSize) = NormalizePage(page, pageSize);
        long offset = checked(((long)normalizedPage - 1) * normalizedSize);
        FontRepositoryPage<AnalysisBatch> result = await _repository.LoadAnalysisBatchPageAsync(offset, normalizedSize, cancellationToken).ConfigureAwait(false);
        var items = new List<AnalysisBatchDto>();
        foreach (var batch in result.Items)
        {
            items.Add(await GetAnalysisStateAsync(batch, cancellationToken).ConfigureAwait(false));
        }

        return new PagedResult<AnalysisBatchDto>(items, normalizedPage, normalizedSize, result.TotalCount);
    }

    /// <summary>Loads safe pageable direct duplicate groups from one persisted analysis report.</summary>
    /// <param name="query">The validated pageable group query.</param>
    /// <param name="cancellationToken">A token that cancels repository reads.</param>
    /// <returns>The bounded group page.</returns>
    /// <exception cref="ArgumentException">Thrown when a supplied filter or sort is not allowlisted.</exception>
    public async Task<PagedResult<FontGroupDto>> GetGroupsAsync(FontGroupQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        (int page, int pageSize) = NormalizePage(query.Page, query.PageSize);
        DuplicateGroupMemberDecision? decision = ParseDecision(query.Decision);
        string sort = NormalizeSort(query.Sort);
        AnalysisBatch? batch = query.AnalysisBatchId.HasValue
            ? await _repository.GetAnalysisBatchAsync(query.AnalysisBatchId.Value, cancellationToken).ConfigureAwait(false)
            : await _repository.GetLatestCompleteAnalysisAsync(cancellationToken).ConfigureAwait(false);
        if (batch is null)
        {
            return new PagedResult<FontGroupDto>([], page, pageSize, 0);
        }

        IEnumerable<DuplicateGroupRecord> groups = batch.Groups;
        if (decision.HasValue)
        {
            groups = groups.Where(group => group.Members.Any(member => member.Decision == decision.Value));
        }

        groups = sort == "reclaimableBytes"
            ? groups.OrderByDescending(group => group.ReclaimableBytes).ThenBy(group => group.Id)
            : groups.OrderBy(group => group.Id);
        FontGroupDto[] mapped = groups.Select(group => ToDto(batch.Id, group)).ToArray();
        return Page(mapped, page, pageSize);
    }

    /// <summary>Loads pageable relationship and unclassifiable-state rows with safe allowlisted filters.</summary>
    /// <param name="query">The validated pageable report query.</param>
    /// <param name="cancellationToken">A token that cancels repository reads.</param>
    /// <returns>The bounded filtered report page.</returns>
    /// <exception cref="ArgumentException">Thrown when a supplied filter is not allowlisted.</exception>
    public async Task<PagedResult<FontReportItemDto>> GetReportsAsync(FontReportQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        (int page, int pageSize) = NormalizePage(query.Page, query.PageSize);
        if (query.AnalysisBatchId is <= 0)
        {
            throw new ArgumentException("The analysis batch filter must be a positive identifier.", nameof(query));
        }

        FontRelationshipKind? relationship = ParseNamedEnum<FontRelationshipKind>(query.Relationship, "relationship");
        UnclassifiableFilter unclassifiable = ParseUnclassifiable(query.Unclassifiable);
        FontReportAction? action = ParseNamedEnum<FontReportAction>(query.Action, "action");
        string? directory = NormalizeDirectory(query.Directory);
        string? technology = NormalizeTechnology(query.Technology);
        AnalysisBatch? batch = query.AnalysisBatchId.HasValue
            ? await _repository.GetAnalysisBatchAsync(query.AnalysisBatchId.Value, cancellationToken).ConfigureAwait(false)
            : await _repository.GetLatestCompleteAnalysisAsync(cancellationToken).ConfigureAwait(false);
        if (batch is null)
        {
            return new PagedResult<FontReportItemDto>([], page, pageSize, 0);
        }

        IReadOnlyList<FontFileRecord> files = await _repository.LoadInventoryAsync(batch.GenerationId, cancellationToken).ConfigureAwait(false);
        Dictionary<long, (FontFileRecord File, FontFaceRecord Face)> faces = files
            .SelectMany(file => file.Faces.Select(face => (face.Id, Value: (File: file, Face: face))))
            .ToDictionary(pair => pair.Id, pair => pair.Value);
        ILookup<long, DuplicateGroupMemberRecord> membersByFace = batch.Groups
            .SelectMany(group => group.Members)
            .ToLookup(member => member.FaceId);
        Dictionary<long, FontFaceAnalysisRecord> scoresByFace = batch.FaceScores.ToDictionary(score => score.FaceId);
        var rows = new List<FontReportItemDto>(batch.Relations.Count + files.Count);
        var replacementReports = FontReportReplacementProjector.Project(batch, files);
        rows.AddRange(replacementReports.Rows);
        foreach (FontRelationRecord relation in batch.Relations.Where(relation => !replacementReports.CoveredRelations.Contains(relation)))
        {
            var evidence = new List<(FontFileRecord File, FontFaceRecord Face)>(2);
            if (faces.TryGetValue(relation.LeftFaceId, out var left))
            {
                evidence.Add(left);
            }

            if (faces.TryGetValue(relation.RightFaceId, out var right))
            {
                evidence.Add(right);
            }

            rows.Add(new FontReportItemDto(
                batch.Id,
                relation.Id,
                relation.RelationshipKind,
                relation.RelationshipKind == FontRelationshipKind.Unclassifiable,
                GetAction(relation.RelationshipKind, membersByFace[relation.LeftFaceId].Concat(membersByFace[relation.RightFaceId])),
                evidence.Select(item => item.File.Id).Distinct().Order().ToArray(),
                evidence.Select(item => item.Face.Id).Distinct().Order().ToArray(),
                evidence.Select(item => item.File.Path).Distinct(FontPathComparison.Comparer).Order(FontPathComparison.Comparer).ToArray(),
                evidence.Select(item => Path.GetDirectoryName(item.File.Path) ?? string.Empty).Distinct(FontPathComparison.Comparer).Order(FontPathComparison.Comparer).ToArray(),
                evidence.Select(item => item.Face.Technology).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
                null,
                ToPairEvidence(relation, faces),
                evidence.Select(item => item.Face.Id)
                    .Where(scoresByFace.ContainsKey)
                    .Select(faceId => ToReportScore(scoresByFace[faceId]))
                    .ToArray(),
                membersByFace[relation.LeftFaceId].Concat(membersByFace[relation.RightFaceId])
                    .Select(member => member.Reason)
                    .Where(reason => !string.IsNullOrWhiteSpace(reason))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()));
        }

        rows.AddRange(files.Where(file => file.Health == FontFileHealth.Unclassifiable).Select(file => new FontReportItemDto(
            batch.Id,
            null,
            null,
            true,
            FontReportAction.Retain,
            [file.Id],
            file.Faces.Select(face => face.Id).Order().ToArray(),
            [file.Path],
            [Path.GetDirectoryName(file.Path) ?? string.Empty],
            file.Faces.Select(face => face.Technology).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            file.Error,
            null,
            file.Faces.Select(face => face.Id)
                .Where(scoresByFace.ContainsKey)
                .Select(faceId => ToReportScore(scoresByFace[faceId]))
                .ToArray(),
            [])));

        IEnumerable<FontReportItemDto> filtered = rows;
        if (relationship.HasValue)
        {
            filtered = filtered.Where(item => item.RelationshipKind == relationship.Value || item.Replacement?.RelationshipKinds.Contains(relationship.Value) == true);
        }

        filtered = unclassifiable switch
        {
            UnclassifiableFilter.Only => filtered.Where(item => item.IsUnclassifiable),
            UnclassifiableFilter.Exclude => filtered.Where(item => !item.IsUnclassifiable),
            _ => filtered
        };
        if (directory is not null)
        {
            filtered = filtered.Where(item => item.Directories.Any(candidate => FontPathComparison.Comparer.Equals(NormalizeDirectory(candidate), directory)));
        }

        if (technology is not null)
        {
            filtered = filtered.Where(item => item.Technologies.Contains(technology, StringComparer.Ordinal));
        }

        if (action.HasValue)
        {
            filtered = filtered.Where(item => item.Action == action.Value);
        }

        FontReportItemDto[] ordered = filtered
            .OrderBy(item => item.IsUnclassifiable)
            .ThenBy(item => item.Replacement is null)
            .ThenByDescending(item => item.Replacement?.ReclaimableBytes ?? 0)
            .ThenBy(item => item.Replacement?.GroupId ?? item.RelationId ?? long.MaxValue)
            .ThenBy(item => item.FileIds.Count == 0 ? 0 : item.FileIds[0])
            .ToArray();
        return Page(ordered, page, pageSize);
    }

    /// <summary>Loads one safe structured duplicate-group detail from persisted data.</summary>
    /// <param name="analysisBatchId">The persisted owning analysis batch identifier.</param>
    /// <param name="groupId">The persisted group identifier.</param>
    /// <param name="cancellationToken">A token that cancels repository reads.</param>
    /// <returns>The group detail, or <see langword="null"/> when either identifier is absent.</returns>
    public async Task<FontGroupDetailDto?> GetGroupDetailAsync(long analysisBatchId, long groupId, CancellationToken cancellationToken)
    {
        AnalysisBatch? batch = await _repository.GetAnalysisBatchAsync(analysisBatchId, cancellationToken).ConfigureAwait(false);
        DuplicateGroupRecord? group = batch?.Groups.FirstOrDefault(candidate => candidate.Id == groupId);
        if (batch is null || group is null)
        {
            return null;
        }

        IReadOnlyList<FontFileRecord> files = await _repository.LoadInventoryAsync(batch.GenerationId, cancellationToken).ConfigureAwait(false);
        Dictionary<long, (FontFileRecord File, FontFaceRecord Face)> faceFiles = files
            .SelectMany(file => file.Faces.Select(face => (face.Id, Value: (File: file, Face: face))))
            .ToDictionary(pair => pair.Id, pair => pair.Value);
        HashSet<long> faces = group.Members.Select(member => member.FaceId).ToHashSet();
        PairEvidenceDto[] pairs = batch.Relations
            .Where(relation => faces.Contains(relation.LeftFaceId) && faces.Contains(relation.RightFaceId))
            .Select(relation => ToPairEvidence(relation, faceFiles))
            .ToArray();
        string[] constraints = pairs.SelectMany(pair => pair.EvidenceKeys)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        string[] skipReasons = group.Members
            .Where(member => member.Decision != DuplicateGroupMemberDecision.QuarantineCandidate)
            .Select(member => member.Reason)
            .Where(reason => !string.IsNullOrWhiteSpace(reason))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        FontGroupDto groupDto = ToDto(batch.Id, group);
        FontGroupMemberDto[] members = groupDto.Members
            .Select(member => faceFiles.TryGetValue(member.FaceId, out var inventory)
                ? member with { Face = ToFaceComparison(inventory.File, inventory.Face) }
                : member)
            .ToArray();
        return new FontGroupDetailDto(groupDto with { Members = members }, pairs, constraints, skipReasons);
    }

    /// <summary>Loads pageable durable maintenance batches.</summary>
    /// <param name="page">The requested one-based page.</param>
    /// <param name="pageSize">The requested page size.</param>
    /// <param name="kinds">The optional allowlisted persisted operation kinds to include.</param>
    /// <param name="cancellationToken">A token that cancels the repository read.</param>
    /// <returns>The bounded maintenance-batch page.</returns>
    public async Task<PagedResult<MaintenanceBatchDto>> GetMaintenanceBatchesAsync(int page, int pageSize, IReadOnlySet<FontOperationKind>? kinds, CancellationToken cancellationToken)
    {
        (int normalizedPage, int normalizedSize) = NormalizePage(page, pageSize);
        long offset = checked(((long)normalizedPage - 1) * normalizedSize);
        FontRepositoryPage<MaintenanceBatch> result = await _repository.LoadMaintenanceBatchPageAsync(offset, normalizedSize, kinds, cancellationToken).ConfigureAwait(false);
        return new PagedResult<MaintenanceBatchDto>(result.Items.Select(ToDto).ToArray(), normalizedPage, normalizedSize, result.TotalCount);
    }

    /// <summary>Loads the latest active quarantine state for each source path across batches.</summary>
    /// <param name="page">The requested one-based page.</param>
    /// <param name="pageSize">The requested page size.</param>
    /// <param name="status">The pending, quarantined, or failed status group.</param>
    /// <param name="cancellationToken">A token that cancels the repository read.</param>
    /// <returns>The bounded quarantine item page.</returns>
    public async Task<PagedResult<QuarantineItemDto>> GetQuarantineItemsPageAsync(int page, int pageSize, string status, CancellationToken cancellationToken)
    {
        IReadOnlySet<QuarantineItemStatus> statuses = status?.Trim().ToLowerInvariant() switch
        {
            "pending" => new HashSet<QuarantineItemStatus> { QuarantineItemStatus.Pending, QuarantineItemStatus.Staged, QuarantineItemStatus.Promoted, QuarantineItemStatus.Cancelled },
            "quarantined" => new HashSet<QuarantineItemStatus> { QuarantineItemStatus.Quarantined, QuarantineItemStatus.Restoring },
            "failed" => new HashSet<QuarantineItemStatus> { QuarantineItemStatus.EvidenceDrift, QuarantineItemStatus.ReplacementMissing, QuarantineItemStatus.DestinationOccupied, QuarantineItemStatus.FailedVerification, QuarantineItemStatus.FailedIo },
            _ => throw new ArgumentException("The quarantine status must be pending, quarantined, or failed.", nameof(status))
        };
        (int normalizedPage, int normalizedSize) = NormalizePage(page, pageSize);
        long offset = checked(((long)normalizedPage - 1) * normalizedSize);
        FontRepositoryPage<QuarantineItemRecord> result = await _repository.LoadQuarantineItemPageAsync(offset, normalizedSize, statuses, cancellationToken).ConfigureAwait(false);
        return new PagedResult<QuarantineItemDto>(result.Items.Select(ToDto).ToArray(), normalizedPage, normalizedSize, result.TotalCount);
    }

    /// <summary>Loads persisted quarantine items for one durable maintenance batch.</summary>
    /// <param name="batchId">The persisted maintenance batch identifier.</param>
    /// <param name="cancellationToken">A token that cancels the repository read.</param>
    /// <returns>The safe item list, or <see langword="null"/> when absent.</returns>
    public async Task<IReadOnlyList<QuarantineItemDto>?> GetQuarantineItemsAsync(long batchId, CancellationToken cancellationToken)
    {
        MaintenanceBatch? batch = await _repository.GetMaintenanceBatchAsync(batchId, cancellationToken).ConfigureAwait(false);
        return batch is not { Kind: FontOperationKind.Quarantine }
            ? null
            : batch.Items.Select(item => ToDto(item with { MaintenanceBatchId = batch.Id })).ToArray();
    }

    /// <summary>Loads a safe operation request status by persisted identifier.</summary>
    /// <param name="requestId">The persisted operation request identifier.</param>
    /// <param name="cancellationToken">A token that cancels the repository read.</param>
    /// <returns>The safe status, or <see langword="null"/> when absent.</returns>
    public async Task<OperationRequestDto?> GetOperationAsync(long requestId, CancellationToken cancellationToken)
    {
        OperationRequest? request = await _repository.GetOperationRequestAsync(requestId, cancellationToken).ConfigureAwait(false);
        return request is null ? null : ToDto(request);
    }

    /// <summary>Loads safe database health diagnostics.</summary>
    /// <param name="cancellationToken">A token that cancels the diagnostic query.</param>
    /// <returns>The safe health snapshot.</returns>
    public async Task<DatabaseHealthDto> GetDatabaseHealthAsync(CancellationToken cancellationToken)
    {
        FontDatabaseHealth health = await _repository.GetDatabaseHealthAsync(cancellationToken).ConfigureAwait(false);
        return ToDto(health);
    }

    /// <summary>Loads bounded persisted performance samples.</summary>
    /// <param name="maximumCount">The requested maximum sample count.</param>
    /// <param name="cancellationToken">A token that cancels the repository read.</param>
    /// <returns>The bounded samples.</returns>
    public async Task<IReadOnlyList<PerformanceSampleDto>> GetPerformanceSamplesAsync(int maximumCount, CancellationToken cancellationToken)
    {
        IReadOnlyList<PerformanceSample> samples = await _repository.LoadRecentPerformanceSamplesAsync(Math.Clamp(maximumCount, 1, MaximumPageSize), cancellationToken).ConfigureAwait(false);
        return samples.Select(ToDto).ToArray();
    }

    /// <summary>Explains why a saved analysis can or cannot authorize current work.</summary>
    /// <param name="batch">Saved report.</param>
    /// <param name="cancellationToken">Cancels current input reads.</param>
    /// <returns>The report with explicit stale reasons.</returns>
    public async Task<AnalysisBatchDto> GetAnalysisStateAsync(AnalysisBatch batch, CancellationToken cancellationToken)
    {
        var health = await _repository.GetDatabaseHealthAsync(cancellationToken).ConfigureAwait(false);
        var latest = await _repository.GetLatestCompleteAnalysisAsync(cancellationToken).ConfigureAwait(false);
        var reasons = new List<string>();
        if (batch.Status != AnalysisBatchStatus.Complete)
        {
            reasons.Add(batch.Status == AnalysisBatchStatus.Stale ? "files-changed" : "analysis-incomplete");
        }

        if (batch.GenerationId != health.PublishedGenerationId)
        {
            reasons.Add("generation-superseded");
        }

        if (latest?.Id != batch.Id)
        {
            reasons.Add("analysis-superseded");
        }

        if (_policy is not null)
        {
            var saved = await _repository.GetPublishedAnalysisInputAsync(cancellationToken).ConfigureAwait(false);
            var configured = FontInventoryInputComparer.Capture([], _policy(), PhysicalFontAnalysisEvidenceSource.AnalysisCompatibilityKey);
            if (saved?.PolicyFingerprint != configured.PolicyFingerprint)
            {
                reasons.Add("directory-policy-changed");
            }

            if (saved?.EvidenceFingerprint != configured.EvidenceFingerprint || batch.EvidenceVersion != FontDuplicateAnalyzer.CurrentEvidenceVersion)
            {
                reasons.Add("evidence-version-changed");
            }
        }

        return ToDto(batch) with { IsCurrent = reasons.Count == 0, StaleReasons = reasons };
    }

    internal static QuarantineItemDto ToDto(QuarantineItemRecord item) => new(item.Id, item.MaintenanceBatchId, item.FileId, item.Status, item.SourcePath, item.QuarantinePath, item.QuarantinedUtc, item.ExpiresUtc, item.RestoredUtc, item.Error);

    private static FontScanOutcomeDto? ReadScanResult(OperationRequest? request)
        {
        FontScanOutcome? result = request?.ResultJson is string json
            ? JsonSerializer.Deserialize<FontScanOutcome>(json, JsonSerializerOptions.Web) : null;
        return result is null ? null : new FontScanOutcomeDto(result.GenerationId, result.AnalysisBatchId, result.Changed, result.ChangeReasons);
    }

    internal static OperationRequestDto ToDto(OperationRequest request) => new(request.Id, request.Kind, request.Status, request.AnalysisBatchId, request.CreatedUtc, request.ClaimedUtc, request.CompletedUtc, request.Error) { ScanResult = ReadScanResult(request) };

    internal static AnalysisBatchDto ToDto(AnalysisBatch batch) => new(batch.Id, batch.GenerationId, batch.Status, batch.StartedUtc, batch.CompletedUtc, batch.CandidateCount, batch.ComparisonCount, batch.ReclaimableBytes, batch.Groups.Count);

    internal static MaintenanceBatchDto ToDto(MaintenanceBatch batch) => new(batch.Id, batch.OperationRequestId, batch.Kind, batch.Status, batch.StartedUtc, batch.CompletedUtc, batch.Items.Count + batch.RenameItems.Count);

    internal static DatabaseHealthDto ToDto(FontDatabaseHealth health) => new(health.SchemaVersion, health.JournalMode, health.Size, health.PublishedGenerationId, health.CheckedUtc, health.RecoveredCorruptDatabasePath);

    internal static PerformanceSampleDto ToDto(PerformanceSample sample) => new(sample.GenerationId, sample.Phase, sample.Duration, sample.ItemCount, sample.ReusedCount, sample.RecordedUtc);

    private static FontGroupDto ToDto(long batchId, DuplicateGroupRecord group) => new(group.Id, batchId, group.RetainedFaceId, group.ReclaimableBytes, group.Members.Select(member => new FontGroupMemberDto(member.FileId, member.FaceId, member.Decision, member.RetentionScore, member.Reason, ReadScore(member.ScoreJson))).ToArray());

    private static (int Page, int PageSize) NormalizePage(int page, int pageSize) => (Math.Max(page, 1), Math.Clamp(pageSize, 1, MaximumPageSize));

    private static PagedResult<T> Page<T>(IReadOnlyList<T> items, int page, int pageSize)
    {
        long offset = checked(((long)page - 1) * pageSize);
        T[] selected = offset >= items.Count ? [] : items.Skip((int)offset).Take(pageSize).ToArray();
        return new PagedResult<T>(selected, page, pageSize, items.Count);
    }

    private static DuplicateGroupMemberDecision? ParseDecision(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value switch
        {
            "retained" => DuplicateGroupMemberDecision.Retained,
            "quarantineCandidate" => DuplicateGroupMemberDecision.QuarantineCandidate,
            _ => throw new ArgumentException("The group decision filter is not supported.", nameof(value))
        };
    }

    private static string NormalizeSort(string? value) => string.IsNullOrWhiteSpace(value) || value == "id" ? "id" : value == "reclaimableBytes" ? value : throw new ArgumentException("The group sort field is not supported.", nameof(value));

    private static T? ParseNamedEnum<T>(string? value, string filterName)
        where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        string? name = Enum.GetNames<T>().FirstOrDefault(candidate => string.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase));
        return name is not null && Enum.TryParse(name, out T parsed)
            ? parsed
            : throw new ArgumentException($"The {filterName} filter is not supported.", nameof(value));
    }

    private static UnclassifiableFilter ParseUnclassifiable(string? value)
    {
        return string.IsNullOrWhiteSpace(value) || string.Equals(value, "all", StringComparison.OrdinalIgnoreCase)
            ? UnclassifiableFilter.All
            : string.Equals(value, "only", StringComparison.OrdinalIgnoreCase)
                ? UnclassifiableFilter.Only
                : string.Equals(value, "exclude", StringComparison.OrdinalIgnoreCase)
                    ? UnclassifiableFilter.Exclude
                    : throw new ArgumentException("The unclassifiable-state filter is not supported.", nameof(value));
    }

    private static string? NormalizeDirectory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return trimmed.Length <= 4096 && trimmed.IndexOf('\0', StringComparison.Ordinal) < 0
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(trimmed))
            : throw new ArgumentException("The directory filter is not supported.", nameof(value));
    }

    private static string? NormalizeTechnology(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string trimmed = value.Trim();
        return Technologies
            .FirstOrDefault(candidate => string.Equals(candidate, trimmed, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("The technology filter is not supported.", nameof(value));
    }

    private static FontReportAction GetAction(FontRelationshipKind relationship, IEnumerable<DuplicateGroupMemberRecord> members)
    {
        if (relationship is FontRelationshipKind.IdenticalFileHash or
            FontRelationshipKind.StrictFaceDuplicate or
            FontRelationshipKind.RenderingAndLayoutEquivalent or
            FontRelationshipKind.MetadataOnlyDifference or
            FontRelationshipKind.CoverageSuperset
            && members.Any(member => member.Decision == DuplicateGroupMemberDecision.QuarantineCandidate))
        {
            return FontReportAction.QuarantineCandidate;
        }

        return relationship switch
        {
            FontRelationshipKind.CoverageVariant or
            FontRelationshipKind.SmallGlyphDifference or
            FontRelationshipKind.LayoutOnlyDifference or
            FontRelationshipKind.HintingOnlyDifference => FontReportAction.ReportOnly,
            FontRelationshipKind.VersionConflict or
            FontRelationshipKind.SameIdentityContentConflict or
            FontRelationshipKind.PostScriptNameConflict or
            FontRelationshipKind.CrossNameConflict => FontReportAction.Conflict,
            _ => FontReportAction.Retain
        };
    }

    private static PairEvidenceDto ToPairEvidence(FontRelationRecord relation, Dictionary<long, (FontFileRecord File, FontFaceRecord Face)> faceFiles)
    {
        string? collectionImpact = CollectionImpact(relation, faceFiles);
        string[] fallbackTypes = [relation.RelationshipKind.ToString()];
        PairEvidenceDto Build(
            string[] keys,
            uint[] differing,
            bool leftCanReplace,
            bool rightCanReplace,
            string[] differenceTypes,
            string? leftDiagnostic = null,
            string? rightDiagnostic = null)
        {
            faceFiles.TryGetValue(relation.LeftFaceId, out var left);
            faceFiles.TryGetValue(relation.RightFaceId, out var right);
            string[] sharedNames = left.Face is null || right.Face is null
                ? []
                : left.Face.Names.Intersect(right.Face.Names, StringComparer.OrdinalIgnoreCase)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .Take(12)
                    .ToArray();
            return new PairEvidenceDto(
                relation.LeftFaceId,
                relation.RightFaceId,
                relation.RelationshipKind,
                relation.ReplacementDirection,
                relation.EvidenceVersion,
                keys,
                differing,
                leftCanReplace,
                rightCanReplace,
                collectionImpact,
                differenceTypes.Length == 0 ? fallbackTypes : differenceTypes)
            {
                LeftFace = left.Face is null ? null : ToFaceComparison(left.File, left.Face),
                RightFace = right.Face is null ? null : ToFaceComparison(right.File, right.Face),
                SharedNames = sharedNames,
                LeftDiagnostic = leftDiagnostic,
                RightDiagnostic = rightDiagnostic
            };
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(relation.EvidenceJson);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Build([], [], false, false, []);
            }

            string[] keys = ReadStringArray(root, "evidenceKeys");
            string[] differenceTypes = ReadStringArray(root, "differenceTypes");
            uint[] differing = root.TryGetProperty("differingCodePoints", out JsonElement points) && points.ValueKind == JsonValueKind.Array
                ? points.EnumerateArray().Where(point => point.TryGetUInt32(out _)).Select(point => point.GetUInt32()).ToArray()
                : [];
            bool leftCanReplace = root.TryGetProperty("leftCanReplaceRight", out JsonElement leftValue) && leftValue.ValueKind == JsonValueKind.True;
            bool rightCanReplace = root.TryGetProperty("rightCanReplaceLeft", out JsonElement rightValue) && rightValue.ValueKind == JsonValueKind.True;
            return Build(keys, differing, leftCanReplace, rightCanReplace, differenceTypes, ReadString(root, "leftDiagnostic"), ReadString(root, "rightDiagnostic"));
        }
        catch (JsonException)
        {
            return Build([], [], false, false, []);
        }
    }

    private static FontFaceComparisonDto ToFaceComparison(FontFileRecord file, FontFaceRecord face)
        => new(
            face.Id,
            file.Path,
            face.FamilyName,
            face.StyleName,
            face.Weight,
            face.Width,
            face.IsItalic,
            face.NameRecords.FirstOrDefault(name => name.NameId == 6)?.Value,
            face.Version,
            Convert.ToHexString(face.StrictFingerprint.Span));

    private static string? ReadString(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? CollectionImpact(FontRelationRecord relation, Dictionary<long, (FontFileRecord File, FontFaceRecord Face)> faceFiles)
    {
        long[] pairFaces = [relation.LeftFaceId, relation.RightFaceId];
        return string.Join("; ", pairFaces
            .Where(faceFiles.ContainsKey)
            .Select(faceId => faceFiles[faceId].File)
            .Where(file => file.Faces.Count > 1)
            .DistinctBy(file => file.Id)
            .Select(file => FormattableString.Invariant($"File {file.Id} contains {file.Faces.Count} Faces; {file.Faces.Count - file.Faces.Count(face => pairFaces.Contains(face.Id))} other Faces share the file"))) is { Length: > 0 } impact
                ? impact
                : null;
    }

    private static FontReportScoreDto ToReportScore(FontFaceAnalysisRecord score)
    {
        IReadOnlyDictionary<string, FontVerificationStatus> verification;
        try
        {
            verification = JsonSerializer.Deserialize<Dictionary<string, FontVerificationStatus>>(score.VerificationJson)
                ?? new Dictionary<string, FontVerificationStatus>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            verification = new Dictionary<string, FontVerificationStatus>(StringComparer.Ordinal);
        }

        return new FontReportScoreDto(score.FaceId, score.RetentionScore, ReadScore(score.ScoreJson), verification);
    }

    private static RetentionScoreBreakdownDto ReadScore(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            return new RetentionScoreBreakdownDto(ReadInt(root, "content"), ReadInt(root, "version"), ReadInt(root, "technical"), ReadInt(root, "names"), ReadInt(root, "modified"));
        }
        catch (JsonException)
        {
            return new RetentionScoreBreakdownDto(0, 0, 0, 0, 0);
        }
    }

    private static string[] ReadStringArray(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().Where(element => element.ValueKind == JsonValueKind.String).Select(element => element.GetString()!).ToArray() : [];

    private static int ReadInt(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result) ? result : 0;
}
