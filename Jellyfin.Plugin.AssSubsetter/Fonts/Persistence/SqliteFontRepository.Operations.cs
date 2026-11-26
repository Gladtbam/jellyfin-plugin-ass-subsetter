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

/// <content>Ordinary request, generation and performance persistence.</content>
public sealed partial class SqliteFontRepository
{
    /// <summary>Creates an unpublished inventory generation.</summary>
    /// <param name="input">The complete scan input.</param>
    /// <param name="changeReasons">Reasons for replacing the current publication.</param>
    /// <param name="cancellationToken">A token that cancels generation creation.</param>
    /// <returns>The new generation identifier.</returns>
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

    /// <summary>Gets the inputs associated with the current publication.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The published inputs, or null before initialization.</returns>
    public async Task<FontAnalysisInputSnapshot?> GetPublishedAnalysisInputAsync(CancellationToken cancellationToken)
    {
        FontDbContext context = await _database.OpenContextAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = context.ConfigureAwait(false);
        string? json = await context.Set<ScanGenerationRow>()
            .Where(row => context.Set<SchemaInfoRow>().Any(schema => schema.SingletonId == 1 && schema.PublishedGenerationId == row.Id))
            .Select(row => row.InputJson).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return json is null ? null : JsonSerializer.Deserialize<FontAnalysisInputSnapshot>(json);
    }

    /// <summary>Checks whether a generation has a durably completed duplicate analysis.</summary>
    /// <param name="generationId">The staged generation to check.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns><see langword="true"/> only when a complete analysis batch exists.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="generationId"/> is zero or negative.</exception>
    public async Task<bool> HasCompleteAnalysisAsync(long generationId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generationId);
        FontDbContext context = await _database.OpenContextAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = context.ConfigureAwait(false);
        return await context.Set<AnalysisBatchRow>().AnyAsync(row => row.GenerationId == generationId && row.Status == (int)AnalysisBatchStatus.Complete, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads reusable complete deep evidence for an unchanged physical file.</summary>
    /// <param name="file">The current generation's file identity and inventory fingerprint.</param>
    /// <param name="evidenceKey">The parser and native-environment compatibility key.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The stored payload, or <see langword="null"/> when no compatible evidence exists.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="file"/> is <see langword="null"/>.</exception>
    public async Task<string?> LoadReusableDeepEvidenceAsync(FontFileRecord file, string evidenceKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
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

    /// <summary>Loads filename authorizations still pending or running.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Active immutable requests.</returns>
    public async Task<IReadOnlyList<OperationRequest>> LoadActiveRenameRequestsAsync(CancellationToken cancellationToken)
    {
        FontDbContext context = await _database.OpenContextAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = context.ConfigureAwait(false);
        List<OperationRequestRow> rows = await context.Set<OperationRequestRow>()
            .Where(row => row.OperationKind == (int)FontOperationKind.NormalizeNames && (row.Status == (int)OperationRequestStatus.Pending || row.Status == (int)OperationRequestStatus.Running))
            .OrderBy(row => row.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(ToOperationRequest)];
    }

    /// <summary>Loads filename requests including failures before batch allocation.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>Saved filename authorizations.</returns>
    public async Task<IReadOnlyList<OperationRequest>> LoadRenameStateRequestsAsync(CancellationToken cancellationToken)
    {
        FontDbContext context = await _database.OpenContextAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = context.ConfigureAwait(false);
        List<OperationRequestRow> rows = await context.Set<OperationRequestRow>().Where(row => row.OperationKind == (int)FontOperationKind.NormalizeNames)
            .OrderBy(row => row.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(ToOperationRequest)];
    }

    /// <summary>Loads the distinct operation kinds with durable pending authorization.</summary>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The operation kinds that still need processing.</returns>
    public async Task<IReadOnlyList<FontOperationKind>> LoadPendingOperationKindsAsync(CancellationToken cancellationToken)
    {
        FontDbContext context = await _database.OpenContextAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = context.ConfigureAwait(false);
        List<int> kinds = await context.Set<OperationRequestRow>().Where(row => row.Status == (int)OperationRequestStatus.Pending)
            .Select(row => row.OperationKind).Distinct().OrderBy(kind => kind).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. kinds.Select(kind => (FontOperationKind)kind)];
    }

    /// <summary>Loads the latest successful analysis operation with its scan outcome.</summary>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The latest completed operation, if any.</returns>
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

    /// <summary>Loads one immutable operation request by identifier.</summary>
    /// <param name="requestId">The persisted request identifier.</param>
    /// <param name="cancellationToken">A token that cancels the query.</param>
    /// <returns>The request, or <see langword="null"/> when it does not exist.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="requestId"/> is zero or negative.</exception>
    public async Task<OperationRequest?> GetOperationRequestAsync(long requestId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(requestId);
        FontDbContext context = await _database.OpenContextAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = context.ConfigureAwait(false);
        OperationRequestRow? row = await context.Set<OperationRequestRow>().SingleOrDefaultAsync(row => row.Id == requestId, cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToOperationRequest(row);
    }

    /// <summary>Loads the persisted operation matching a trimmed idempotency key.</summary>
    /// <param name="idempotencyKey">The client retry key.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The matching request, or <see langword="null"/> if the key is unknown.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="idempotencyKey"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="idempotencyKey"/> is empty or consists only of whitespace.</exception>
    public async Task<OperationRequest?> GetOperationRequestByIdempotencyKeyAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        string normalizedKey = idempotencyKey.Trim();
        FontDbContext context = await _database.OpenContextAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = context.ConfigureAwait(false);
        OperationRequestRow? row = await context.Set<OperationRequestRow>().SingleOrDefaultAsync(row => row.IdempotencyKey == normalizedKey, cancellationToken).ConfigureAwait(false);
        return row is null ? null : ToOperationRequest(row);
    }

    /// <summary>Persists the lifecycle state of an existing operation request.</summary>
    /// <param name="request">The persisted request with its next state.</param>
    /// <param name="cancellationToken">A token that cancels the update.</param>
    /// <returns>A task representing the update.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The identifier in <paramref name="request"/> is zero or negative.</exception>
    /// <exception cref="InvalidOperationException">The specified operation request does not exist.</exception>
    public Task UpdateOperationAsync(OperationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
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

    /// <summary>Marks completed reports for one inventory generation stale.</summary>
    /// <param name="generationId">The superseded inventory generation identifier.</param>
    /// <param name="cancellationToken">A token that cancels the update.</param>
    /// <returns>A task representing the guarded status update.</returns>
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

    /// <summary>Persists one local inventory or analysis performance sample.</summary>
    /// <param name="sample">The measured phase sample.</param>
    /// <param name="cancellationToken">A token that cancels persistence.</param>
    /// <returns>A task representing the write.</returns>
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

    /// <summary>Loads the newest recorded performance samples, using sample identifiers to break timestamp ties.</summary>
    /// <param name="maximumCount">The maximum number of samples, from 1 through 1,000.</param>
    /// <param name="cancellationToken">Cancels the query.</param>
    /// <returns>The performance samples ordered newest first.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maximumCount"/> is outside the supported range.</exception>
    public async Task<IReadOnlyList<PerformanceSample>> LoadRecentPerformanceSamplesAsync(int maximumCount, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumCount, 1000);
        FontDbContext context = await _database.OpenContextAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = context.ConfigureAwait(false);
        List<PerformanceSampleRow> rows = await context.Set<PerformanceSampleRow>().OrderByDescending(row => row.RecordedUtc).ThenByDescending(row => row.Id)
            .Take(maximumCount).ToListAsync(cancellationToken).ConfigureAwait(false);
        return
        [
            .. rows.Select(row => new PerformanceSample
            {
                GenerationId = row.GenerationId,
                Phase = row.Phase,
                Duration = TimeSpan.FromTicks(row.DurationTicks),
                ItemCount = row.ItemCount,
                ReusedCount = row.ReusedCount,
                RecordedUtc = ParseDate(row.RecordedUtc)
            })
        ];
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
