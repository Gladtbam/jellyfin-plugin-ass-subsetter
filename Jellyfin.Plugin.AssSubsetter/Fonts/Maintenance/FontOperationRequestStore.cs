using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

/// <summary>Creates immutable, durable idempotent requests for physical-file maintenance and fileless scheduled Analyze or Purge operations.</summary>
public sealed class FontOperationRequestStore
{
    private readonly IFontRepository _repository;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="FontOperationRequestStore"/> class.</summary>
    /// <param name="repository">The durable font repository.</param>
    /// <param name="timeProvider">The UTC clock used for immutable creation timestamps.</param>
    public FontOperationRequestStore(IFontRepository repository, TimeProvider timeProvider)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>Creates an idempotent maintenance request containing only persisted physical-file identifiers.</summary>
    /// <param name="kind">The requested maintenance operation.</param>
    /// <param name="batchId">The immutable analysis or maintenance batch that owns the selection.</param>
    /// <param name="selectedFileIds">The selected persisted physical-file identifiers.</param>
    /// <param name="idempotencyKey">The administrator-generated unique retry key.</param>
    /// <param name="cancellationToken">A token that cancels request persistence.</param>
    /// <returns>The new request, or the existing identical request for the key.</returns>
    /// <exception cref="ArgumentException">Thrown when the selection or key is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when a batch or file identifier is not positive.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the key already identifies a different immutable request.</exception>
    public async Task<OperationRequest> CreateAsync(
        FontOperationKind kind,
        long batchId,
        IReadOnlyList<long> selectedFileIds,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        if (kind == FontOperationKind.NormalizeNames)
        {
            throw new ArgumentException("Filename execution requires server-computed authorization.", nameof(kind));
        }

        string payloadJson = FontOperationRequestPayloadCodec.CreateSelectionJson(batchId, selectedFileIds);
        var request = new OperationRequest
        {
            Kind = kind,
            Status = OperationRequestStatus.Pending,
            AnalysisBatchId = kind == FontOperationKind.Quarantine ? batchId : null,
            IdempotencyKey = idempotencyKey.Trim(),
            RequestJson = payloadJson,
            CreatedUtc = _timeProvider.GetUtcNow()
        };
        OperationRequest persisted = await _repository.CreateOperationRequestAsync(request, cancellationToken).ConfigureAwait(false);
        if (persisted.Kind != request.Kind || !string.Equals(persisted.RequestJson, request.RequestJson, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The idempotency key already identifies a different immutable request.");
        }

        return persisted;
    }

    /// <summary>Persists exact server-side evidence and targets for authorized file moves.</summary>
    /// <param name="payload">The immutable rules and published references.</param>
    /// <param name="idempotencyKey">The unique retry key.</param>
    /// <param name="cancellationToken">Cancels persistence.</param>
    /// <returns>The new request or the identical existing request.</returns>
    public async Task<OperationRequest> CreateRenameExecutionAsync(FontRenameExecutionPayload payload, string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        var request = new OperationRequest
        {
            Kind = FontOperationKind.NormalizeNames,
            Status = OperationRequestStatus.Pending,
            AnalysisBatchId = payload.AnalysisBatchId,
            IdempotencyKey = idempotencyKey.Trim(),
            RequestJson = FontOperationRequestPayloadCodec.CreateRenameExecutionJson(payload),
            CreatedUtc = _timeProvider.GetUtcNow()
        };
        OperationRequest persisted = await _repository.CreateOperationRequestAsync(request, cancellationToken).ConfigureAwait(false);
        if (persisted.Kind != request.Kind || persisted.RequestJson != request.RequestJson)
        {
            throw new InvalidOperationException("The idempotency key already identifies a different immutable request.");
        }

        return persisted;
    }

    /// <summary>Finds an existing immutable selection request only when its key identifies the exact proposed operation.</summary>
    /// <param name="kind">The requested maintenance operation.</param>
    /// <param name="batchId">The immutable analysis or maintenance batch that owns the selection.</param>
    /// <param name="selectedFileIds">The selected persisted physical-file identifiers.</param>
    /// <param name="idempotencyKey">The administrator-generated unique retry key.</param>
    /// <param name="cancellationToken">A token that cancels the repository lookup.</param>
    /// <returns>The existing matching request, or <see langword="null"/> when the key is unused.</returns>
    /// <exception cref="ArgumentException">Thrown when the selection or key is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when a batch or file identifier is not positive.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the key identifies a different immutable request.</exception>
    public async Task<OperationRequest?> FindMatchingAsync(
        FontOperationKind kind,
        long batchId,
        IReadOnlyList<long> selectedFileIds,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        string payloadJson = FontOperationRequestPayloadCodec.CreateSelectionJson(batchId, selectedFileIds);
        OperationRequest? existing = await _repository.GetOperationRequestByIdempotencyKeyAsync(idempotencyKey.Trim(), cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return null;
        }

        if (existing.Kind != kind || !string.Equals(existing.RequestJson, payloadJson, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The idempotency key already identifies a different immutable request.");
        }

        return existing;
    }

    /// <summary>Creates an idempotent scheduled task request that intentionally selects no physical files.</summary>
    /// <param name="kind">The supported scheduled operation.</param>
    /// <param name="idempotencyKey">The unique retry key for this scheduled invocation.</param>
    /// <param name="cancellationToken">A token that cancels request persistence.</param>
    /// <returns>The new request, or the existing identical request for the key.</returns>
    /// <exception cref="ArgumentException">Thrown when the operation is not a task-only operation or the key is empty.</exception>
    public async Task<OperationRequest> CreateTaskAsync(
        FontOperationKind kind,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (kind is not FontOperationKind.Analyze and not FontOperationKind.Purge and not FontOperationKind.RebuildDerivedData)
        {
            throw new ArgumentException("Only analysis, purge, and derived-data rebuild can be created without a physical file selection.", nameof(kind));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        var request = new OperationRequest
        {
            Kind = kind,
            Status = OperationRequestStatus.Pending,
            IdempotencyKey = idempotencyKey.Trim(),
            RequestJson = "{}",
            CreatedUtc = _timeProvider.GetUtcNow()
        };
        OperationRequest persisted = await _repository.CreateOperationRequestAsync(request, cancellationToken).ConfigureAwait(false);
        if (persisted.Kind != request.Kind || !string.Equals(persisted.RequestJson, request.RequestJson, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The idempotency key already identifies a different immutable request.");
        }

        return persisted;
    }
}
