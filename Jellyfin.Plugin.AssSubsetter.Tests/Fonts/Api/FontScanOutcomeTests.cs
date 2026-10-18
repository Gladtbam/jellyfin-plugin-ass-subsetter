using System.Text.Json;
using Jellyfin.Plugin.AssSubsetter.Fonts.Api;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Api;

public sealed class FontScanOutcomeTests
{
    [Fact]
    public void DomainOutcomePreservesHistoricalJsonAndApiProjection()
    {
        const string json = "{\"generationId\":2,\"analysisBatchId\":3,\"changed\":false,\"changeReasons\":[\"files-changed\"]}";
        var outcome = JsonSerializer.Deserialize<FontScanOutcome>(json, JsonSerializerOptions.Web)!;
        Assert.Equal(2, outcome.GenerationId);
        Assert.Equal(3, outcome.AnalysisBatchId);
        Assert.False(outcome.Changed);
        Assert.Equal(new[] { "files-changed" }, outcome.ChangeReasons);
        Assert.Equal(json, JsonSerializer.Serialize(outcome, JsonSerializerOptions.Web));
        var dto = FontManagementQuery.ToDto(new OperationRequest { ResultJson = json, IdempotencyKey = "legacy", RequestJson = "{}" });
        Assert.Equal(outcome.GenerationId, dto.ScanResult!.GenerationId);
        Assert.Equal(outcome.AnalysisBatchId, dto.ScanResult.AnalysisBatchId);
        Assert.Equal(outcome.Changed, dto.ScanResult.Changed);
        Assert.Equal(outcome.ChangeReasons, dto.ScanResult.ChangeReasons);
    }
}
