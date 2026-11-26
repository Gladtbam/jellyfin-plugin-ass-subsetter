using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Persistence;

public sealed partial class SqliteFontRepositoryTests
{
    [Fact]
    public async Task OptimizeDatabase_ShouldReclaimFreePagesAndKeepPublishedInventory()
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        long publishedGeneration = await SeedCompactionFixtureAsync(fixture, token);
        FontFileRecord retained = Assert.Single(await fixture.Repository.LoadPublishedInventoryAsync(token));
        long before = new FileInfo(fixture.Database.DatabasePath).Length;

        var result = await fixture.Repository.OptimizeDatabaseAsync(token);

        Assert.Null(result.DeferredReason);
        Assert.True(result.ReclaimedBytes >= 4L * 1024 * 1024);
        Assert.Equal(result.ReclaimedBytes, before - new FileInfo(fixture.Database.DatabasePath).Length);
        FontFileRecord file = Assert.Single(await fixture.Repository.LoadPublishedInventoryAsync(token));
        Assert.Equal(retained.Id, file.Id);
        Assert.Equal(publishedGeneration, file.GenerationId);
        Assert.Equal("retained.ttf", file.Path);
        Assert.Equal(Assert.Single(retained.Faces).Id, Assert.Single(file.Faces).Id);
        await using var connection = await fixture.Database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_stat1 WHERE tbl = 'font_file';";
        Assert.True(Assert.IsType<long>(await command.ExecuteScalarAsync(token)) > 0);
        command.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", await command.ExecuteScalarAsync(token));
        command.CommandText = "PRAGMA foreign_key_check;";
        Assert.Null(await command.ExecuteScalarAsync(token));

        var second = await fixture.Repository.OptimizeDatabaseAsync(token);
        Assert.Equal((0L, null), second);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1849:Call async methods when in an async method", Justification = "Only the synchronous SQLite overload can open a deferred reader without taking the writer lock.")]
    public async Task OptimizeDatabase_ShouldDeferWhileAReaderPinsTheWal()
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        CancellationToken token = TestContext.Current.CancellationToken;
        await SeedCompactionFixtureAsync(fixture, token);
        await using (var connection = await fixture.Database.OpenConnectionAsync(token))
        {
            await using var transaction = connection.BeginTransaction(deferred: true);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM scan_generation;";
            Assert.Equal(1L, await command.ExecuteScalarAsync(token));
            await fixture.Repository.BeginGenerationAsync(FontAnalysisInputSnapshot.Empty, [], token);

            var deferred = await fixture.Repository.OptimizeDatabaseAsync(token);

            Assert.Equal((0L, "database-busy"), deferred);
            Assert.Equal(1L, await command.ExecuteScalarAsync(token));
        }

        var retried = await fixture.Repository.OptimizeDatabaseAsync(token);
        Assert.Null(retried.DeferredReason);
        Assert.True(retried.ReclaimedBytes > 0);
        Assert.Single(await fixture.Repository.LoadPublishedInventoryAsync(token));
    }

    private static async Task<long> SeedCompactionFixtureAsync(SqliteRepositoryFixture fixture, CancellationToken token)
    {
        long generation = await fixture.Repository.BeginGenerationAsync(FontAnalysisInputSnapshot.Empty, [], token);
        await fixture.Repository.WriteInventoryAsync(generation, [CreateFile("retained.ttf", "Retained")], token);
        await fixture.Repository.PublishAnalysisAsync(new AnalysisBatch
        {
            GenerationId = generation, Status = AnalysisBatchStatus.Complete, SummaryJson = "{\"faceScores\":[]}"
        }, token);
        await using var connection = await fixture.Database.OpenConnectionAsync(token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE compaction_fixture (id INTEGER PRIMARY KEY, payload BLOB);
            INSERT INTO compaction_fixture VALUES (1, zeroblob(8388608));
            DROP TABLE compaction_fixture;
            PRAGMA wal_checkpoint(TRUNCATE);
            """;
        await command.ExecuteNonQueryAsync(token);
        return generation;
    }
}
