using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Persistence;

public sealed partial class SqliteFontRepositoryTests
{
    [Theory]
    [InlineData("existing-key")]
    [InlineData(" existing-key ")]
    [InlineData("\t existing-key \r\n")]
    public async Task OperationLookup_ShouldNormalizeIdempotencyKey(string key)
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        OperationRequest saved = await fixture.Repository.CreateOperationRequestAsync(new OperationRequest
        {
            Kind = FontOperationKind.Analyze,
            Status = OperationRequestStatus.Pending,
            IdempotencyKey = "existing-key",
            RequestJson = "{}",
            CreatedUtc = DateTimeOffset.UnixEpoch
        }, CancellationToken.None);

        OperationRequest? loaded = await fixture.Repository.GetOperationRequestByIdempotencyKeyAsync(key, CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(saved.Id, loaded.Id);
        Assert.Equal("existing-key", loaded.IdempotencyKey);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(40)]
    public async Task InventoryQueries_ShouldRemainBoundedAndPreserveAllFaceEvidence(int fileCount)
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        long generation = await fixture.Repository.BeginGenerationAsync(FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        FontFileRecord[] files = Enumerable.Range(0, fileCount).Select(index => CreateFile($"file-{index:D3}.ttf", $"Family {index}") with
        {
            PathAliases = [$"alias-{index}.ttf"],
            Faces = [CreateFile("", $"Family {index}").Faces[0] with
            {
                UnicodeCoverage = [new UnicodeRange(0x20, 0x7e)]
            }, CreateFile("", $"Other {index}").Faces[0] with { FaceIndex = 1 }]
        }).ToArray();
        await fixture.Repository.WriteInventoryAsync(generation, files, CancellationToken.None);
        await fixture.Repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = generation, Status = AnalysisBatchStatus.Complete, SummaryJson = "{\"faceScores\":[]}" }, CancellationToken.None);

        using var queries = new EfQueryListener(fixture.Database.DatabasePath);
        IReadOnlyList<FontFileRecord> actual = await fixture.Repository.LoadPublishedInventoryForReuseAsync(CancellationToken.None);
        Assert.Equal(fileCount, actual.Count);
        Assert.Equal(fileCount * 2, actual.Sum(file => file.Faces.Count));
        Assert.All(actual, file =>
        {
            Assert.Single(file.PathAliases);
            Assert.Equal([0, 1], file.Faces.Select(face => face.FaceIndex));
            Assert.Equal(new UnicodeRange(0x20, 0x7e), Assert.Single(file.Faces[0].UnicodeCoverage));
            Assert.Single(file.Faces[0].Names);
        });
        Assert.InRange(queries.Count, 1, 7);
    }


    [Theory]
    [InlineData(1)]
    [InlineData(40)]
    public async Task RuntimeQueries_ShouldJoinFaceEvidenceAndRemainBounded(int fileCount)
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        long generation = await fixture.Repository.BeginGenerationAsync(FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        FontFileRecord[] files = Enumerable.Range(0, fileCount).Select(index => CreateFile($"file-{index:D3}.ttf", $"Family {index}") with
        {
            Faces = [CreateFile("", $"Family {index}").Faces[0] with
            {
                NameRecords = [new OpenTypeNameRecord(3, 1, 0x409, 1, $"Family {index}")],
                UnicodeCoverage = [new UnicodeRange(0x20, 0x7e)]
            }, CreateFile("", $"Other {index}").Faces[0] with { FaceIndex = 1 }]
        }).ToArray();
        await fixture.Repository.WriteInventoryAsync(generation, files, CancellationToken.None);
        await fixture.Repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = generation, Status = AnalysisBatchStatus.Complete, SummaryJson = "{\"faceScores\":[]}" }, CancellationToken.None);
        using var queries = new EfQueryListener(fixture.Database.DatabasePath);

        IReadOnlyList<RuntimeFontFace> faces = await fixture.Repository.LoadPublishedRuntimeFacesAsync(CancellationToken.None);

        Assert.Equal(fileCount * 2, faces.Count);
        for (int index = 0; index < fileCount; index++)
        {
            Assert.Equal($"Family {index}", Assert.Single(faces[index * 2].Names));
            Assert.Equal($"Family {index}", Assert.Single(faces[index * 2].NameRecords).Value);
            Assert.Equal(new UnicodeRange(0x20, 0x7e), Assert.Single(faces[index * 2].UnicodeCoverage));
            Assert.Equal($"Other {index}", Assert.Single(faces[(index * 2) + 1].Names));
            Assert.Empty(faces[(index * 2) + 1].NameRecords);
            Assert.Empty(faces[(index * 2) + 1].UnicodeCoverage);
        }

        Assert.InRange(queries.Count, 1, 3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InventoryReadSnapshot_ShouldSurviveConcurrentPublicationAndPruning(bool runtime)
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        long oldGeneration = await fixture.Repository.BeginGenerationAsync(FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        FontFileRecord original = CreateFile("old.ttf", "Old") with
        {
            PathAliases = ["old-alias.ttf"],
            Faces = [CreateFile("", "Old").Faces[0] with
            {
                NameRecords = [new OpenTypeNameRecord(3, 1, 0x409, 1, "Old")],
                UnicodeCoverage = [new UnicodeRange(0x20, 0x7e)]
            }]
        };
        await fixture.Repository.WriteInventoryAsync(oldGeneration, [original], CancellationToken.None);
        await fixture.Repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = oldGeneration, Status = AnalysisBatchStatus.Complete, SummaryJson = "{\"faceScores\":[]}" }, CancellationToken.None);
        long newGeneration = await fixture.Repository.BeginGenerationAsync(FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await fixture.Repository.WriteInventoryAsync(newGeneration, [CreateFile("new.ttf", "New")], CancellationToken.None);
        using var queries = new EfQueryListener(fixture.Database.DatabasePath, () =>
        {
            fixture.Repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = newGeneration, Status = AnalysisBatchStatus.Complete, SummaryJson = "{\"faceScores\":[]}" }, CancellationToken.None).GetAwaiter().GetResult();
            Assert.Equal(1, fixture.Repository.PruneAnalysisBatchesAsync(1, CancellationToken.None).GetAwaiter().GetResult());
            Assert.Equal(1, fixture.Repository.PruneObsoleteGenerationsAsync(1, DateTimeOffset.MaxValue, null, CancellationToken.None).GetAwaiter().GetResult());
        });
        if (runtime)
        {
            RuntimeFontFace face = Assert.Single(await fixture.Repository.LoadPublishedRuntimeFacesAsync(CancellationToken.None));
            Assert.Equal("old.ttf", face.Path);
            Assert.Equal("Old", Assert.Single(face.Names));
            Assert.Equal("Old", Assert.Single(face.NameRecords).Value);
            Assert.Single(face.UnicodeCoverage);
        }
        else
        {
            FontFileRecord file = Assert.Single(await fixture.Repository.LoadPublishedInventoryForReuseAsync(CancellationToken.None));
            Assert.Equal(oldGeneration, file.GenerationId);
            Assert.Equal("old-alias.ttf", Assert.Single(file.PathAliases));
            FontFaceRecord face = Assert.Single(file.Faces);
            Assert.Equal("Old", Assert.Single(face.Names));
            Assert.Equal("Old", Assert.Single(face.NameRecords).Value);
            Assert.Single(face.UnicodeCoverage);
        }

        Assert.Equal(newGeneration, (await fixture.Repository.GetDatabaseHealthAsync(CancellationToken.None)).PublishedGenerationId);
        Assert.Empty(await fixture.Repository.LoadInventoryAsync(oldGeneration, CancellationToken.None));
    }

    [Fact]
    public async Task HistoryPages_ShouldAcceptOffsetsBeyondInt32WithoutWrapping()
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        long generation = await fixture.Repository.BeginGenerationAsync(FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await fixture.Repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = generation, Status = AnalysisBatchStatus.Complete, SummaryJson = "{\"faceScores\":[]}" }, CancellationToken.None);
        var page = await fixture.Repository.LoadAnalysisBatchPageAsync((long)int.MaxValue + 1, 10, CancellationToken.None);
        Assert.Equal(1, page.TotalCount);
        Assert.Empty(page.Items);
        Assert.Empty((await fixture.Repository.LoadMaintenanceBatchPageAsync(long.MaxValue, 10, null, CancellationToken.None)).Items);
        Assert.Empty((await fixture.Repository.LoadQuarantineItemPageAsync(long.MaxValue, 10, new HashSet<QuarantineItemStatus> { QuarantineItemStatus.Quarantined }, CancellationToken.None)).Items);
    }


    [Fact]
    public async Task QuarantinePageCount_ShouldRankHistoryWithoutCorrelatedScans()
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        OperationRequest request = await fixture.Repository.CreateOperationRequestAsync(new OperationRequest
        {
            Kind = FontOperationKind.Quarantine, Status = OperationRequestStatus.Complete,
            IdempotencyKey = "quarantine-count-plan", RequestJson = "{}", CreatedUtc = DateTimeOffset.UnixEpoch
        }, CancellationToken.None);
        MaintenanceBatch batch = await fixture.Repository.SaveMaintenanceBatchAsync(new MaintenanceBatch
        {
            OperationRequestId = request.Id, Kind = FontOperationKind.Quarantine,
            Status = MaintenanceBatchStatus.Complete, StartedUtc = DateTimeOffset.UnixEpoch, SummaryJson = "{}"
        }, CancellationToken.None);
        await fixture.Repository.UpdateMaintenanceBatchAsync(batch with
        {
            Items = Enumerable.Range(1, 40).Select(index => new QuarantineItemRecord
            {
                FileId = index, SourcePath = $"source-{index}.ttf", StagingPath = $"staging-{index}.ttf",
                QuarantinePath = $"quarantine-{index}.ttf", Sha256 = new byte[] { 1 }, Status = QuarantineItemStatus.Quarantined
            }).ToArray()
        }, CancellationToken.None);
        string? countSql = null;
        (string Name, object Value)[] parameters = [];
        using var queries = new EfQueryListener(fixture.Database.DatabasePath, onCommand: command =>
        {
            if (command.CommandText.Contains("COUNT(", StringComparison.OrdinalIgnoreCase))
            {
                countSql = command.CommandText;
                parameters = command.Parameters.Cast<DbParameter>().Select(parameter => (parameter.ParameterName, parameter.Value!)).ToArray();
            }
        });
        var page = await fixture.Repository.LoadQuarantineItemPageAsync(0, 10,
            new HashSet<QuarantineItemStatus> { QuarantineItemStatus.Quarantined }, CancellationToken.None);
        Assert.Equal(40, page.TotalCount);
        Assert.Equal(10, page.Items.Count);
        Assert.NotNull(countSql);
        await using var connection = await fixture.Database.OpenConnectionAsync(CancellationToken.None);
        await using var explain = connection.CreateCommand();
        explain.CommandText = "EXPLAIN QUERY PLAN " + countSql;
        foreach (var parameter in parameters)
        {
            explain.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        var plans = new List<string>();
        await using var reader = await explain.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            plans.Add(reader.GetString(3));
        }

        Assert.DoesNotContain(plans, plan => plan.Contains("CORRELATED", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RuntimeScoreUpdate_ShouldCheckOneFileWithoutMaterializingTheWholeGeneration()
    {
        await using var fixture = await SqliteRepositoryFixture.CreateAsync();
        long generation = await fixture.Repository.BeginGenerationAsync(FontAnalysisInputSnapshot.Empty, [], CancellationToken.None);
        await fixture.Repository.WriteInventoryAsync(generation, [CreateFile("one.ttf", "One"), CreateFile("two.ttf", "Two")], CancellationToken.None);
        FontFileRecord file = (await fixture.Repository.LoadInventoryAsync(generation, CancellationToken.None))[0];
        await using var connection = await fixture.Database.OpenConnectionAsync(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + Jellyfin.Plugin.AssSubsetter.Fonts.Persistence.SqliteFontRepository.RuntimeScoreUpdateSql;
        command.Parameters.AddWithValue("$score", 100);
        command.Parameters.AddWithValue("$preferred", 1);
        command.Parameters.AddWithValue("$faceId", file.Faces[0].Id);
        command.Parameters.AddWithValue("$fileId", file.Id);
        command.Parameters.AddWithValue("$generationId", generation);
        var plans = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            plans.Add(reader.GetString(3));
        }

        Assert.DoesNotContain(plans, plan => plan.Contains("LIST SUBQUERY", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(plans, plan => plan.Contains("INTEGER PRIMARY KEY", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class EfQueryListener : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly string _databasePath;
        private readonly ConcurrentBag<IDisposable> _subscriptions = [];
        private int _count;
        private readonly Action? _onFirstQuery;
        private readonly Action<DbCommand>? _onCommand;

        internal EfQueryListener(string databasePath, Action? onFirstQuery = null, Action<DbCommand>? onCommand = null)
        {
            _databasePath = databasePath;
            _onFirstQuery = onFirstQuery;
            _onCommand = onCommand;
            _subscriptions.Add(DiagnosticListener.AllListeners.Subscribe(this));
        }

        internal int Count => Volatile.Read(ref _count);

        public void OnNext(DiagnosticListener value)
        {
            if (value.Name == "Microsoft.EntityFrameworkCore")
            {
                _subscriptions.Add(value.Subscribe(this));
            }
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Key.EndsWith(".CommandExecuted", StringComparison.Ordinal)
                && value.Value?.GetType().GetProperty("Command")?.GetValue(value.Value) is DbCommand command
                && command.Connection?.DataSource == _databasePath)
            {
                _onCommand?.Invoke(command);
                if (command.CommandText.StartsWith("SELECT", StringComparison.Ordinal) && Interlocked.Increment(ref _count) == 1)
                {
                    _onFirstQuery?.Invoke();
                }
            }
        }

        public void OnError(Exception error) { }

        public void OnCompleted() { }

        public void Dispose()
        {
            foreach (IDisposable subscription in _subscriptions)
            {
                subscription.Dispose();
            }
        }
    }
}
