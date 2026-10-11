using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

/// <summary>Persists candidate refreshes independently of analysis publication.</summary>
#pragma warning disable CA1711 // Durable operation queue, not a collection.
public sealed class FontRenameCandidateRefreshQueue
{
    private readonly IFontRepository _repository;
    private readonly Func<FontNamingRules> _rules;
    private readonly Func<FontRenameCandidateReadRequirements> _requirements;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a new instance of the <see cref="FontRenameCandidateRefreshQueue"/> class.</summary>
    /// <param name="repository">Durable requests and current context.</param>
    /// <param name="rules">Current administrator rules.</param>
    /// <param name="requirements">Compatible analysis inputs.</param>
    /// <param name="timeProvider">Request clock.</param>
    public FontRenameCandidateRefreshQueue(IFontRepository repository, Func<FontNamingRules> rules, Func<FontRenameCandidateReadRequirements> requirements, TimeProvider timeProvider)
    {
        _repository = repository;
        _rules = rules;
        _requirements = requirements;
        _timeProvider = timeProvider;
    }

    /// <summary>Accepts an explicit recalculation, retaining the exact original retry identity.</summary>
    /// <param name="idempotencyKey">Original operation key.</param>
    /// <param name="cancellationToken">Cancels acceptance.</param>
    /// <returns>The persisted original request.</returns>
    public async Task<OperationRequest> QueueAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        return await QueueCoreAsync(idempotencyKey.Trim(), false, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("A complete compatible current font analysis is required.");
    }

    /// <summary>Schedules missing or outdated candidates, without retrying a failed current context.</summary>
    /// <param name="cancellationToken">Cancels acceptance.</param>
    /// <returns>The accepted refresh, or null when no work is needed.</returns>
    public Task<OperationRequest?> EnsureCurrentAsync(CancellationToken cancellationToken)
        => QueueCoreAsync("rename-refresh:" + Guid.NewGuid().ToString("N"), true, cancellationToken);

    internal Task RecoverInterruptedAsync(CancellationToken token) => _repository.RecoverRenameCandidateRefreshAsync(token);

    private async Task<OperationRequest?> QueueCoreAsync(string key, bool onlyIfNeeded, CancellationToken token)
    {
        OperationRequest? existing = await _repository.GetOperationRequestByIdempotencyKeyAsync(key, token).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.Kind != FontOperationKind.RefreshRenameCandidates)
            {
                throw new InvalidOperationException("The idempotency key belongs to another operation.");
            }

            return existing;
        }

        var rules = _rules();
        var requirements = _requirements();
        if (rules.GetFingerprint() != requirements.RuleFingerprint)
        {
            throw new InvalidOperationException("Naming rules changed during acceptance.");
        }

        var context = await _repository.GetRenameCandidateContextAsync(requirements, token).ConfigureAwait(false);
        if (context.Publication is null)
        {
            return null;
        }

        var payload = new FontRenameCandidateRefreshPayload(context.Publication.GenerationId, context.Publication.AnalysisBatchId, requirements.RuleFingerprint, requirements.CalculatorVersion, null);
        var request = new OperationRequest
        {
            Kind = FontOperationKind.RefreshRenameCandidates,
            IdempotencyKey = key,
            Status = OperationRequestStatus.Pending,
            AnalysisBatchId = payload.AnalysisBatchId,
            RequestJson = System.Text.Json.JsonSerializer.Serialize(payload, System.Text.Json.JsonSerializerOptions.Web),
            CreatedUtc = _timeProvider.GetUtcNow()
        };
        return await _repository.CreateRenameCandidateRefreshAsync(request, rules, requirements, onlyIfNeeded, token).ConfigureAwait(false);
    }
}
