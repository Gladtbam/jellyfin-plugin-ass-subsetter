using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

namespace Jellyfin.Plugin.AssSubsetter.ScheduledTasks;

/// <summary>Runs candidate calculation within the existing shared font task gate.</summary>
public sealed partial class FontMaintenanceTaskExecutor
{
    private async Task<FontTaskExecutionResult> RunCandidateRefreshAsync(CancellationToken cancellationToken)
    {
        if (!TryEnterTask(cancellationToken))
        {
            return new(null, "task-active");
        }

        try
        {
            var group = await _repository.ClaimRenameCandidateRefreshGroupAsync(cancellationToken).ConfigureAwait(false);
            if (group.Count == 0)
            {
                return new(null, "request-missing");
            }

            var first = group[0];
            var payload = SqliteFontRepository.ParseRenameRefresh(first.RequestJson);
            long rootId = payload.CalculationRequestId ?? first.Id;
            try
            {
                var requirements = _candidateRequirements?.Invoke() ?? throw new InvalidOperationException("Candidate calculator is unavailable.");
                var context = await _repository.GetRenameCandidateContextAsync(requirements, cancellationToken).ConfigureAwait(false);
                if (!SqliteFontRepository.RefreshMatchesPublication(context, payload, requirements) || context.State?.OperationRequestId != rootId)
                {
                    await CompleteCandidateGroupAsync(group, OperationRequestStatus.Cancelled, "The candidate refresh was superseded.", null).ConfigureAwait(false);
                    if (_ensureCandidates is not null)
                    {
                        await _ensureCandidates(cancellationToken).ConfigureAwait(false);
                    }

                    return new(null, "superseded", first.Id);
                }

                if (context.IsCurrent)
                {
                    await CompleteCandidateGroupAsync(group, OperationRequestStatus.Complete, null, JsonSerializer.Serialize(context.State, JsonOptions)).ConfigureAwait(false);
                    return new(null, "reused-calculation", first.Id);
                }

                FontRenameCandidateState state;
                var root = await _repository.GetOperationRequestAsync(rootId, cancellationToken).ConfigureAwait(false);
                if (root is not null && root.Status is OperationRequestStatus.Complete or OperationRequestStatus.Failed or OperationRequestStatus.Cancelled)
                {
                    await CompleteCandidateGroupAsync(group, root.Status, root.Error, root.ResultJson).ConfigureAwait(false);
                    return new(null, "reused-calculation", first.Id);
                }

                state = await (_rebuildCandidates ?? throw new InvalidOperationException("Candidate calculator is unavailable."))(rootId, cancellationToken).ConfigureAwait(false);
                var status = state.Status == FontRenameCandidateGenerationStatus.Ready ? OperationRequestStatus.Complete : OperationRequestStatus.Failed;
                await CompleteCandidateGroupAsync(group, status, state.Error, JsonSerializer.Serialize(state, JsonOptions)).ConfigureAwait(false);
                return new(null, state.Status.ToString().ToLowerInvariant(), first.Id);
            }
            catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
            {
                await MarkCandidateFailureAsync(rootId, "The candidate refresh was cancelled.").ConfigureAwait(false);
                await CompleteCandidateGroupAsync(group, OperationRequestStatus.Cancelled, "The candidate refresh was cancelled.", null).ConfigureAwait(false);
                AddOperationRequestIdentifiers(exception, first);
                throw;
            }
            catch (Exception exception)
            {
                await MarkCandidateFailureAsync(rootId, exception.Message).ConfigureAwait(false);
                await CompleteCandidateGroupAsync(group, OperationRequestStatus.Failed, exception.Message, null).ConfigureAwait(false);
                if (_ensureCandidates is not null)
                {
                    await _ensureCandidates(CancellationToken.None).ConfigureAwait(false);
                }

                AddOperationRequestIdentifiers(exception, first);
                throw;
            }
        }
        finally
        {
            ReleaseTask();
        }
    }

    private async Task MarkCandidateFailureAsync(long rootId, string error)
    {
        if (_candidateRequirements is null)
        {
            return;
        }

        var requirements = _candidateRequirements();
        var context = await _repository.GetRenameCandidateContextAsync(requirements, CancellationToken.None).ConfigureAwait(false);
        if (context.State is not { } state || state.OperationRequestId != rootId || state.Status == FontRenameCandidateGenerationStatus.Stale)
        {
            return;
        }

        try
        {
            await _repository.SetRenameCandidateStateAsync(state with { Status = FontRenameCandidateGenerationStatus.Failed, Error = error }, requirements, CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            var latest = await _repository.GetRenameCandidateContextAsync(_candidateRequirements(), CancellationToken.None).ConfigureAwait(false);
            if (latest.State?.OperationRequestId == rootId && latest.State.Status != FontRenameCandidateGenerationStatus.Stale)
            {
                throw;
            }
        }
    }

    private async Task CompleteCandidateGroupAsync(IReadOnlyList<OperationRequest> group, OperationRequestStatus status, string? error, string? resultJson)
    {
        foreach (var request in group)
        {
            await CompleteRequestAsync(request with { ResultJson = resultJson }, status, error).ConfigureAwait(false);
        }
    }
}
