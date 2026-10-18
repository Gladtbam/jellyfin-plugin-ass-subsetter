using Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Persistence;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Analysis;

public sealed class FontAnalysisWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublicationFollowsPreparationAndFinalValidation(bool failValidation)
    {
        var calls = new List<string>();
        var batch = new AnalysisBatch { GenerationId = 1, Status = AnalysisBatchStatus.Complete, SummaryJson = "{}" };
        var workflow = new FontAnalysisWorkflow(new InMemoryFontRepository(),
            (_, _, _) => { calls.Add("scan"); return Task.FromResult(new FontInventoryScanResult(1, true, FontAnalysisInputSnapshot.Empty, [])); },
            (_, _, _) => { calls.Add("analyze"); return Task.FromResult(batch); },
            (value, _, _) => { calls.Add("publish"); return Task.FromResult(value with { Id = 3 }); },
            (_, _, _) => { calls.Add("validate"); return failValidation ? Task.FromException(new InvalidOperationException("drift")) : Task.CompletedTask; },
            (_, _) => { calls.Add("prepare"); return Task.FromResult(new FontRenameCandidatePreparation(new FontRenameCandidateState(1, 0, new FontNamingRules("{family}", "", false), "rules", 1, FontRenameCandidateGenerationStatus.Ready, null, null, null), [])); });
        if (failValidation)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.RunAsync(7, false, new Progress<double>(), CancellationToken.None));
            Assert.Equal(new[] { "scan", "analyze", "prepare", "validate" }, calls);
        }
        else
        {
            var result = await workflow.RunAsync(7, false, new Progress<double>(), CancellationToken.None);
            Assert.Equal(3, result.Batch.Id);
            Assert.Equal(new[] { "scan", "analyze", "prepare", "validate", "publish" }, calls);
        }
    }
}
