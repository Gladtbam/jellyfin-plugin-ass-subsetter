using System.Text.Json;
using Jellyfin.Plugin.AssSubsetter.Fonts.Api;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Persistence;

public sealed partial class SqliteFontRepositoryTests
{
    [Fact]
    public async Task Reports_ShouldReadEvidenceOnlyForRowsThatReachThePage()
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        AnalysisBatch batch = await PublishPairReportAsync(fixture, token);
        var commands = new List<string>();
        using var queries = new EfQueryListener(fixture.Database.DatabasePath, onCommand: command => commands.Add(command.CommandText));
        var query = new FontManagementQuery(fixture.Repository);

        var emptyPage = await query.GetReportsAsync(new FontReportQuery(3, 1, batch.Id, null, null, null, null, null), token);

        Assert.Equal(2, emptyPage.TotalCount);
        Assert.Empty(emptyPage.Items);
        Assert.DoesNotContain(commands, sql => sql.Contains("\"evidence_json\"", StringComparison.Ordinal)
            || sql.Contains("\"summary_json\"", StringComparison.Ordinal)
            || sql.Contains("\"score_json\"", StringComparison.Ordinal)
            || sql.Contains("\"face_name\"", StringComparison.Ordinal)
            || sql.Contains("\"face_name_evidence\"", StringComparison.Ordinal));

        commands.Clear();
        var page = await query.GetReportsAsync(new FontReportQuery(1, 1, batch.Id, null, null, null, null, null), token);

        Assert.Equal(2, page.TotalCount);
        AssertSelectedPair(Assert.Single(page.Items));
        string relationEvidence = Assert.Single(commands, sql => sql.Contains("\"evidence_json\"", StringComparison.Ordinal));
        Assert.Contains("\"id\" =", relationEvidence, StringComparison.Ordinal);
        var names = commands.Where(sql => sql.Contains("\"face_name\"", StringComparison.Ordinal)
            || sql.Contains("\"face_name_evidence\"", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, names.Length);
        Assert.All(names, sql => Assert.Contains("\"face_id\" IN", sql, StringComparison.Ordinal));
        string scores = Assert.Single(commands, sql => sql.Contains("summary_json", StringComparison.Ordinal));
        Assert.Contains("IN (SELECT value FROM json_each(", scores, StringComparison.Ordinal);
        Assert.DoesNotContain(commands, sql => sql.Contains("\"font_path_alias\"", StringComparison.Ordinal)
            || sql.Contains("\"face_coverage\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReportReadSnapshot_ShouldKeepPageEvidenceDuringPublicationAndPruning()
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        AnalysisBatch batch = await PublishPairReportAsync(fixture, token);
        long newGeneration = await fixture.Repository.BeginGenerationAsync(FontAnalysisInputSnapshot.Empty, [], token);
        await fixture.Repository.WriteInventoryAsync(newGeneration, [CreateFile("new.ttf", "New")], token);
        using var queries = new EfQueryListener(fixture.Database.DatabasePath, () =>
        {
            fixture.Repository.PublishAnalysisAsync(new AnalysisBatch
            {
                GenerationId = newGeneration,
                Status = AnalysisBatchStatus.Complete,
                SummaryJson = "{\"faceScores\":[]}"
            }, token).GetAwaiter().GetResult();
            Assert.Equal(1, fixture.Repository.PruneAnalysisBatchesAsync(1, token).GetAwaiter().GetResult());
            Assert.Equal(1, fixture.Repository.PruneObsoleteGenerationsAsync(1, DateTimeOffset.MaxValue, null, token).GetAwaiter().GetResult());
        });

        var page = await new FontManagementQuery(fixture.Repository)
            .GetReportsAsync(new FontReportQuery(1, 1, null, null, null, null, null, null), token);

        Assert.Equal(batch.Id, Assert.Single(page.Items).AnalysisBatchId);
        AssertSelectedPair(Assert.Single(page.Items));
        Assert.Null(await fixture.Repository.GetAnalysisBatchAsync(batch.Id, token));
    }

    private static async Task<AnalysisBatch> PublishPairReportAsync(SqliteRepositoryFixture fixture, CancellationToken token)
    {
        long generation = await fixture.Repository.BeginGenerationAsync(FontAnalysisInputSnapshot.Empty, [], token);
        FontFileRecord collection = CreateFile("collection.ttc", "Shared");
        await fixture.Repository.WriteInventoryAsync(generation,
        [
            collection with
            {
                Faces =
                [
                    collection.Faces[0] with { NameRecords = [new OpenTypeNameRecord(3, 1, 0x409, 6, "Collection-Primary")] },
                    collection.Faces[0] with { FaceIndex = 1 }
                ]
            },
            CreateFile("copy.ttf", "Shared"),
            CreateFile("other.ttf", "Other")
        ], token);
        var inventory = await fixture.Repository.LoadInventoryAsync(generation, token);
        var primary = inventory[0].Faces[0];
        return await fixture.Repository.PublishAnalysisAsync(new AnalysisBatch
        {
            GenerationId = generation,
            Status = AnalysisBatchStatus.Complete,
            SummaryJson = JsonSerializer.Serialize(new
            {
                faceScores = new[]
                {
                    // The persisted summary reader accepts case-insensitive property names and numeric strings.
                    new { FACEID = primary.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), RetentionScore = 56, ScoreJson = "{\"names\":12}", VerificationJson = "{}" }
                }
            }),
            Relations =
            [
                new FontRelationRecord
                {
                    LeftFaceId = primary.Id,
                    RightFaceId = inventory[1].Faces[0].Id,
                    RelationshipKind = FontRelationshipKind.VersionConflict,
                    EvidenceVersion = 3,
                    EvidenceJson = "{\"evidenceKeys\":[\"name-sharing\"],\"leftDiagnostic\":\"incomplete comparison\"}"
                },
                new FontRelationRecord
                {
                    LeftFaceId = inventory[0].Faces[1].Id,
                    RightFaceId = inventory[2].Faces[0].Id,
                    RelationshipKind = FontRelationshipKind.PostScriptNameConflict,
                    EvidenceJson = "{}"
                }
            ]
        }, token);
    }

    private static void AssertSelectedPair(FontReportItemDto row)
    {
        Assert.Equal(FontReportAction.Conflict, row.Action);
        var pair = Assert.IsType<PairEvidenceDto>(row.PairEvidence);
        Assert.Equal(FontRelationshipKind.VersionConflict, pair.RelationshipKind);
        Assert.Equal(["name-sharing"], pair.EvidenceKeys);
        Assert.Equal(["Shared"], pair.SharedNames);
        Assert.Equal("Collection-Primary", pair.LeftFace?.PostScriptName);
        Assert.Equal("incomplete comparison", pair.LeftDiagnostic);
        Assert.Contains("1 other Faces share the file", pair.CollectionFaceImpact, StringComparison.Ordinal);
        var score = Assert.Single(row.Scores);
        Assert.Equal(56, score.RetentionScore);
        Assert.Equal(12, score.Components.Names);
    }
}
