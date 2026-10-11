using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Microsoft.EntityFrameworkCore;
using static Jellyfin.Plugin.AssSubsetter.Fonts.Persistence.FontDatabaseRows;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <summary>Ordinary request, generation and performance persistence.</summary>
public sealed partial class SqliteFontRepository
{
    /// <inheritdoc/>
    public Task<long> BeginGenerationAsync(FontAnalysisInputSnapshot input, IReadOnlyList<string> changeReasons, CancellationToken cancellationToken)
        => _database.WriteAsync(
            async (connection, token) =>
            {
                FontDbContext context = FontDatabase.CreateContext(connection);
                await using var scope = context.ConfigureAwait(false);
                var row = new ScanGenerationRow { Status = 0, CreatedUtc = FormatDate(DateTimeOffset.UtcNow), InputJson = JsonSerializer.Serialize(input), ChangeReasonsJson = JsonSerializer.Serialize(changeReasons) };
                context.Add(row);
                await context.SaveChangesAsync(token).ConfigureAwait(false);
                return row.Id;
            },
            cancellationToken);

    /// <inheritdoc/>
    public async Task<FontAnalysisInputSnapshot?> GetPublishedAnalysisInputAsync(CancellationToken cancellationToken)
    {
        FontDbContext context = await _database.OpenContextAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = context.ConfigureAwait(false);
        string? json = await context.Set<ScanGenerationRow>()
            .Where(row => context.Set<SchemaInfoRow>().Any(schema => schema.SingletonId == 1 && schema.PublishedGenerationId == row.Id))
            .Select(row => row.InputJson).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return json is null ? null : JsonSerializer.Deserialize<FontAnalysisInputSnapshot>(json);
    }

    /// <inheritdoc/>
    public async Task<bool> HasCompleteAnalysisAsync(long generationId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generationId);
        FontDbContext context = await _database.OpenContextAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = context.ConfigureAwait(false);
        return await context.Set<AnalysisBatchRow>().AnyAsync(row => row.GenerationId == generationId && row.Status == (int)AnalysisBatchStatus.Complete, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<string?> LoadReusableDeepEvidenceAsync(FontFileRecord file, string evidenceKey, CancellationToken cancellationToken)
    {
        FontDbContext context = await _database.OpenContextAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = context.ConfigureAwait(false);
        string modified = file.ModifiedUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        byte[] hash = file.Sha256.ToArray();
        byte[]? payload = await (
            from evidence in context.Set<FontDeepEvidenceRow>()
            join saved in context.Set<FontFileRow>() on evidence.FileId equals saved.Id
            where saved.PhysicalIdentity == file.PhysicalIdentity && saved.Size == file.Size
                && saved.ModifiedUtc == modified && saved.Sha256 == hash && evidence.EvidenceKey == evidenceKey
            orderby evidence.FileId descending
            select evidence.PayloadGzip).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return payload is null ? null : DecompressDeepEvidence(payload);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<OperationRequest>> LoadActiveRenameRequestsAsync(CancellationToken cancellationToken)
    {
        FontDbContext context = await _database.OpenContextAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = context.ConfigureAwait(false);
        List<OperationRequestRow> rows = await context.Set<OperationRequestRow>()
            .Where(row => row.OperationKind == (int)FontOperationKind.NormalizeNames && (row.Status == (int)OperationRequestStatus.Pending || row.Status == (int)OperationRequestStatus.Running))
            .OrderBy(row => row.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(ToOperationRequest).ToArray();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<OperationRequest>> LoadRenameStateRequestsAsync(CancellationToken cancellationToken)
    {
        FontDbContext context = await _database.OpenContextAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = context.ConfigureAwait(false);
        List<OperationRequestRow> rows = await context.Set<OperationRequestRow>().Where(row => row.OperationKind == (int)FontOperationKind.NormalizeNames)
            .OrderBy(row => row.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(ToOperationRequest).ToArray();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<FontOperationKind>> LoadPendingOperationKindsAsync(CancellationToken cancellationToken)
    {
        FontDbContext context = await _database.OpenContextAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = context.ConfigureAwait(false);
        List<int> kinds = await context.Set<OperationRequestRow>().Where(row => row.Status == (int)OperationRequestStatus.Pending)
            .Select(row => row.OperationKind).Distinct().OrderBy(kind => kind).ToListAsync(cancellationToken).ConfigureAwait(false);
        return kinds.Select(kind => (FontOperationKind)kind).ToArray();
    }

    /// <inheritdoc/>
    public async Task<OperationRequest?> GetLatestCompletedAnalysisOperationAsync(CancellationToken cancellationToken)
    {
        FontDbContext context = await _database.OpenContextAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = context.ConfigureAwait(false);
        OperationRequestRow? row = await context.Set<OperationRequestRow>()
            .Where(row => (row.OperationKind == (int)FontOperationKind.Analyze || row.OperationKind == (int)FontOperationKind.RebuildDerivedData)
                && row.Status == (int)OperationRequestStatus.Complete && row.ResultJson != null)
            .OrderByDescending(row => row.CompletedUtc).ThenByDescending(row => row.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToOperationRequest(row);
    }

    /// <inheritdoc/>
    public async Task<OperationRequest?> GetOperationRequestAsync(long requestId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestId);
        FontDbContext context = await _database.OpenContextAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = context.ConfigureAwait(false);
        OperationRequestRow? row = await context.Set<OperationRequestRow>().SingleOrDefaultAsync(row => row.Id == requestId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToOperationRequest(row);
    }

    /// <inheritdoc/>
    public async Task<OperationRequest?> GetOperationRequestByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        string normalizedKey = idempotencyKey.Trim();
        FontDbContext context = await _database.OpenContextAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = context.ConfigureAwait(false);
        OperationRequestRow? row = await context.Set<OperationRequestRow>().SingleOrDefaultAsync(row => row.IdempotencyKey == normalizedKey, cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToOperationRequest(row);
    }

    /// <inheritdoc/>
    public Task UpdateOperationAsync(OperationRequest request, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.Id);
        return _database.WriteAsync(
            async (connection, token) =>
            {
                FontDbContext context = FontDatabase.CreateContext(connection);
                await using var scope = context.ConfigureAwait(false);
                string? claimed = request.ClaimedUtc.HasValue ? FormatDate(request.ClaimedUtc.Value) : null;
                string? completed = request.CompletedUtc.HasValue ? FormatDate(request.CompletedUtc.Value) : null;
                int changed = await context.Set<OperationRequestRow>().Where(row => row.Id == request.Id).ExecuteUpdateAsync(
                    setters => setters
                    .SetProperty(row => row.Status, (int)request.Status).SetProperty(row => row.ClaimedUtc, claimed)
                    .SetProperty(row => row.CompletedUtc, completed).SetProperty(row => row.Error, request.Error)
                    .SetProperty(row => row.ResultJson, request.ResultJson),
                    token).ConfigureAwait(false);
                if (changed != 1)
                {
                    throw new InvalidOperationException("The operation request does not exist.");
                }

                return true;
            },
            cancellationToken);
    }

    /// <inheritdoc/>
    public Task MarkAnalysisStaleAsync(long generationId, CancellationToken cancellationToken)
        => _database.WriteAsync(
            async (connection, token) =>
            {
                FontDbContext context = FontDatabase.CreateContext(connection);
                await using var scope = context.ConfigureAwait(false);
                await context.Set<AnalysisBatchRow>().Where(row => row.GenerationId == generationId && row.Status == (int)AnalysisBatchStatus.Complete)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, (int)AnalysisBatchStatus.Stale), token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    /// <inheritdoc/>
    public Task SavePerformanceSampleAsync(PerformanceSample sample, CancellationToken cancellationToken)
        => _database.WriteAsync(
            async (connection, token) =>
            {
                FontDbContext context = FontDatabase.CreateContext(connection);
                await using var scope = context.ConfigureAwait(false);
                context.Add(new PerformanceSampleRow
                {
                    GenerationId = sample.GenerationId, Phase = sample.Phase, DurationTicks = sample.Duration.Ticks,
                    ItemCount = sample.ItemCount, ReusedCount = sample.ReusedCount, RecordedUtc = FormatDate(sample.RecordedUtc)
                });
                await context.SaveChangesAsync(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PerformanceSample>> LoadRecentPerformanceSamplesAsync(int maximumCount, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumCount, 1000);
        FontDbContext context = await _database.OpenContextAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = context.ConfigureAwait(false);
        List<PerformanceSampleRow> rows = await context.Set<PerformanceSampleRow>().OrderByDescending(row => row.RecordedUtc).ThenByDescending(row => row.Id)
            .Take(maximumCount).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(row => new PerformanceSample
        {
            GenerationId = row.GenerationId, Phase = row.Phase, Duration = TimeSpan.FromTicks(row.DurationTicks),
            ItemCount = row.ItemCount, ReusedCount = row.ReusedCount, RecordedUtc = ParseDate(row.RecordedUtc)
        }).ToArray();
    }

    private static OperationRequest ToOperationRequest(OperationRequestRow row) => new()
    {
        Id = row.Id, Kind = (FontOperationKind)row.OperationKind, Status = (OperationRequestStatus)row.Status,
        AnalysisBatchId = row.AnalysisBatchId, IdempotencyKey = row.IdempotencyKey, RequestJson = row.RequestJson,
        CreatedUtc = ParseDate(row.CreatedUtc), ClaimedUtc = ParseNullableDate(row.ClaimedUtc),
        CompletedUtc = ParseNullableDate(row.CompletedUtc), Error = row.Error, ResultJson = row.ResultJson
    };

    private static DateTimeOffset? ParseNullableDate(string? value) => value is null ? null : ParseDate(value);
}
