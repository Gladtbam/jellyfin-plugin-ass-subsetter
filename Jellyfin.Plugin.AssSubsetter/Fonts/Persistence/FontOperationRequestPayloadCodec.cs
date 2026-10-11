using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance.Naming;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;

/// <summary>Owns the persisted selection format shared by request creation and manifest recovery.</summary>
internal static class FontOperationRequestPayloadCodec
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string CreateSelectionJson(long batchId, IReadOnlyList<long> selectedFileIds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchId);
        ArgumentNullException.ThrowIfNull(selectedFileIds);
        if (selectedFileIds.Count == 0)
        {
            throw new ArgumentException("At least one physical file must be selected.", nameof(selectedFileIds));
        }

        long[] normalizedIds = selectedFileIds.Distinct().Order().ToArray();
        if (normalizedIds.Any(fileId => fileId <= 0))
        {
            throw new ArgumentOutOfRangeException(nameof(selectedFileIds), "Physical-file identifiers must be positive.");
        }

        return JsonSerializer.Serialize(new SelectionPayload(batchId, normalizedIds), JsonOptions);
    }

    public static bool MatchesSelectionJson(string requestJson, long batchId, IReadOnlyList<long> selectedFileIds)
    {
        try
        {
            SelectionPayload? payload = JsonSerializer.Deserialize<SelectionPayload>(requestJson, JsonOptions);
            return payload is not null
                && payload.BatchId == batchId
                && payload.SelectedFileIds is not null
                && payload.SelectedFileIds.SequenceEqual(selectedFileIds.Distinct().Order());
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string CreateRenameExecutionJson(FontRenameExecutionPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.GenerationId <= 0 || payload.AnalysisBatchId <= 0 || payload.Rules is null
            || payload.RuleFingerprint != payload.Rules.GetFingerprint() || FontNamingTemplate.ValidateRules(payload.Rules).Count != 0
            || payload.Items is null || payload.Items.Count == 0
            || payload.Items.Select(item => item.FileId).Distinct().Count() != payload.Items.Count
            || payload.Items.Select(item => item.NewPath).Distinct(FontPathComparison.Comparer).Count() != payload.Items.Count)
        {
            throw new ArgumentException("Invalid immutable filename execution authorization.", nameof(payload));
        }

        foreach (var item in payload.Items)
        {
            if (item.FileId <= 0 || item.Evidence is null || item.Evidence.Id != item.FileId || item.Evidence.GenerationId != payload.GenerationId
                || item.Evidence.Path != item.OldPath || item.Evidence.Sha256.Length != 32
                || !System.IO.Path.IsPathFullyQualified(item.OldPath) || !System.IO.Path.IsPathFullyQualified(item.NewPath) || !System.IO.Path.IsPathFullyQualified(item.SourceRoot)
                || !FontPathComparison.IsWithin(item.OldPath, item.SourceRoot) || !FontPathComparison.IsWithin(item.NewPath, item.SourceRoot)
                || item.Status != RenameItemStatus.Pending || item.Error is not null || item.TemporaryPath.Length != 0)
            {
                throw new ArgumentException("Invalid root-bounded selected file evidence.", nameof(payload));
            }
        }

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    public static FontRenameExecutionPayload ParseRenameExecution(string requestJson)
    {
        FontRenameExecutionPayload payload = JsonSerializer.Deserialize<FontRenameExecutionPayload>(requestJson, JsonOptions)
            ?? throw new ArgumentException("Invalid filename execution payload.", nameof(requestJson));
        _ = CreateRenameExecutionJson(payload);
        return payload;
    }

    public static bool OverlapsRenameAuthorization(FontRenameExecutionPayload left, FontRenameExecutionPayload right)
        => left.Items.Any(a => right.Items.Any(b =>
            FontPathComparison.Comparer.Equals(a.OldPath, b.OldPath) || FontPathComparison.Comparer.Equals(a.NewPath, b.NewPath)
            || FontPathComparison.Comparer.Equals(a.OldPath, b.NewPath) || FontPathComparison.Comparer.Equals(a.NewPath, b.OldPath)
            || (!string.IsNullOrEmpty(a.Evidence.PhysicalIdentity) && a.Evidence.PhysicalIdentity == b.Evidence.PhysicalIdentity)));

    private sealed record SelectionPayload(long BatchId, IReadOnlyList<long> SelectedFileIds);
}
