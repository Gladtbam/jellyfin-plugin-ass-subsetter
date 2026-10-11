using System.Buffers.Binary;
using System.Security.Cryptography;
using Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;
using Jellyfin.Plugin.AssSubsetter.Models;
using Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Fixtures;
using Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Persistence;
using Moq;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Analysis;

public sealed class PhysicalFontAnalysisEvidenceSourceTests
{
    [Fact]
    public async Task LoadFiles_ShouldRejectIncompleteNativeVerificationFromTheRealVerifier()
    {
        string root = Path.Combine(Path.GetTempPath(), "ass-strict-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            FontFileRecord file = await WriteDataAsync(root, "left.ttf", GeneratedAnalysisFont.Create());
            var repository = new InMemoryFontRepository();
            long generation = await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], TestContext.Current.CancellationToken);
            await repository.WriteInventoryAsync(generation, [file with { GenerationId = generation }], TestContext.Current.CancellationToken);

            FontDeepAnalysisEvidence deep = await new PhysicalFontAnalysisEvidenceSource(repository).LoadFilesAsync(generation, [file.Id], TestContext.Current.CancellationToken);

            FontFaceEvidence face = Assert.Single(deep.Faces);
            Assert.NotEmpty(face.CharacterFingerprints);
            KeyValuePair<string, FontVerificationStatus>[] blocked = deep.Files[0].Faces[0].Quality.Verification
                .Where(pair => pair.Key is "skia" or "harfbuzz" or "freetype" or "libass" && pair.Value != FontVerificationStatus.Passed)
                .ToArray();
            Assert.NotEmpty(blocked); // The generated fixture has no renderable outlines for libass.
            Assert.False(face.IsClassifiable);
            Assert.False(face.IsDeepEvidenceComplete);
            Assert.False(deep.Files[0].Faces[0].IsClassifiable);
            foreach ((string check, FontVerificationStatus status) in blocked)
            {
                Assert.Contains(check, face.ClassificationError, StringComparison.Ordinal);
                Assert.Contains(status.ToString(), face.ClassificationError, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("skia", FontVerificationStatus.Unknown, false)]
    [InlineData("skia", FontVerificationStatus.Failed, false)]
    [InlineData("skia", FontVerificationStatus.NotApplicable, false)]
    [InlineData("harfbuzz", FontVerificationStatus.Unknown, false)]
    [InlineData("harfbuzz", FontVerificationStatus.Failed, false)]
    [InlineData("harfbuzz", FontVerificationStatus.NotApplicable, false)]
    [InlineData("freetype", FontVerificationStatus.Unknown, false)]
    [InlineData("freetype", FontVerificationStatus.Failed, false)]
    [InlineData("freetype", FontVerificationStatus.NotApplicable, false)]
    [InlineData("libass", FontVerificationStatus.Unknown, false)]
    [InlineData("libass", FontVerificationStatus.Failed, false)]
    [InlineData("libass", FontVerificationStatus.NotApplicable, false)]
    [InlineData("layout", FontVerificationStatus.Unknown, false)]
    [InlineData("layout", FontVerificationStatus.Failed, false)]
    [InlineData("layout", FontVerificationStatus.NotApplicable, true)]
    [InlineData("vertical", FontVerificationStatus.Unknown, false)]
    [InlineData("vertical", FontVerificationStatus.Failed, false)]
    [InlineData("vertical", FontVerificationStatus.NotApplicable, true)]
    [InlineData("color", FontVerificationStatus.Unknown, false)]
    [InlineData("color", FontVerificationStatus.Failed, false)]
    [InlineData("color", FontVerificationStatus.NotApplicable, true)]
    [InlineData("axes", FontVerificationStatus.Unknown, false)]
    [InlineData("axes", FontVerificationStatus.Failed, false)]
    [InlineData("axes", FontVerificationStatus.NotApplicable, true)]
    [InlineData("checksums", FontVerificationStatus.Unknown, true)]
    [InlineData("checksums", FontVerificationStatus.Failed, true)]
    [InlineData("embeddingRights", FontVerificationStatus.Unknown, true)]
    [InlineData("embeddingRights", FontVerificationStatus.Failed, true)]
    public async Task LoadFiles_ShouldEnforceFunctionalVerificationOnCachedEvidence(string check, FontVerificationStatus status, bool classifiable)
    {
        string root = Path.Combine(Path.GetTempPath(), "ass-strict-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            FontFileRecord file = await WriteDataAsync(root, "left.ttf", GeneratedAnalysisFont.Create());
            FontDeepAnalysisEvidence cached = await CompleteCacheFixtureAsync(file);
            var verification = new Dictionary<string, FontVerificationStatus>(cached.Files[0].Faces[0].Quality.Verification) { [check] = status };
            cached = cached with
            {
                Files = [cached.Files[0] with
                {
                    Faces = [cached.Files[0].Faces[0] with
                    {
                        Quality = cached.Files[0].Faces[0].Quality with { Verification = verification }
                    }]
                }]
            };
            var repository = CachedRepository(file, FontDeepEvidenceJson.Serialize(cached));
            var source = new PhysicalFontAnalysisEvidenceSource(repository.Object);

            FontDeepAnalysisEvidence deep = await source.LoadFilesAsync(1, [file.Id], TestContext.Current.CancellationToken);

            FontFaceEvidence face = Assert.Single(deep.Faces);
            Assert.Equal(classifiable, face.IsClassifiable);
            Assert.Equal(classifiable, face.IsDeepEvidenceComplete);
            Assert.Equal(classifiable, deep.Files[0].Faces[0].IsClassifiable);
            FontRelationshipResult relation = new FontRelationshipClassifier().Classify(face, face with { PhysicalFileId = "other-physical-file" });
            Assert.Equal(classifiable ? FontRelationshipKind.IdenticalFileHash : FontRelationshipKind.Unclassifiable, relation.Kind);
            Assert.Equal(classifiable, relation.LeftCanReplaceRight);
            FontRelationshipResult strictRelation = new FontRelationshipClassifier().Classify(face, face);
            Assert.Equal(classifiable ? FontRelationshipKind.StrictFaceDuplicate : FontRelationshipKind.Unclassifiable, strictRelation.Kind);
            if (classifiable)
            {
                Assert.Null(face.ClassificationError);
            }
            else
            {
                Assert.Contains(check, face.ClassificationError, StringComparison.Ordinal);
                Assert.Contains(status.ToString(), face.ClassificationError, StringComparison.Ordinal);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadFiles_ShouldInspectAgainInsteadOfReusingLegacyVerificationEvidence()
    {
        string root = Path.Combine(Path.GetTempPath(), "ass-legacy-deep-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            FontFileRecord file = await WriteDataAsync(root, "left.ttf", GeneratedAnalysisFont.Create());
            FontDeepAnalysisEvidence cached = await CompleteCacheFixtureAsync(file);
            var repository = CachedRepository(file, null);
            repository.Setup(value => value.LoadReusableDeepEvidenceAsync(It.IsAny<FontFileRecord>(), It.Is<string>(key => key.StartsWith("deep-v6|", StringComparison.Ordinal)), It.IsAny<CancellationToken>()))
                .ReturnsAsync(FontDeepEvidenceJson.Serialize(cached));
            int reads = 0;
            var source = new PhysicalFontAnalysisEvidenceSource(repository.Object, async (path, token) =>
            {
                reads++;
                return await File.ReadAllBytesAsync(path, token);
            });

            FontDeepAnalysisEvidence deep = await source.LoadFilesAsync(1, [file.Id], TestContext.Current.CancellationToken);

            Assert.Equal(1, reads);
            Assert.False(Assert.Single(deep.Faces).IsClassifiable);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadFiles_ShouldReuseInventorySnapshotAcrossCandidateBatches()
    {
        string root = Path.Combine(Path.GetTempPath(), "ass-inventory-reuse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            FontFileRecord left = await WriteDataAsync(root, "left.ttf", GeneratedAnalysisFont.Create());
            FontFileRecord right = await WriteDataAsync(root, "right.ttf", GeneratedAnalysisFont.Create());
            var repository = new InMemoryFontRepository();
            long generation = await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], TestContext.Current.CancellationToken);
            await repository.WriteInventoryAsync(generation,
                [left with { GenerationId = generation }, right with { GenerationId = generation }],
                TestContext.Current.CancellationToken);
            var source = new PhysicalFontAnalysisEvidenceSource(repository);

            await source.LoadAsync(generation, TestContext.Current.CancellationToken);
            await source.LoadFilesAsync(generation, [left.Id], TestContext.Current.CancellationToken);
            await source.LoadFilesAsync(generation, [right.Id], TestContext.Current.CancellationToken);

            Assert.Equal(1, repository.InventoryLoadCallCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
    [Fact]
    public async Task LoadFiles_ShouldReuseCompleteEvidenceAcrossUnchangedStagedGenerations()
    {
        string root = Path.Combine(Path.GetTempPath(), "ass-deep-reuse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            FontFileRecord file = await WriteDataAsync(root, "left.ttf", GeneratedAnalysisFont.Create());
            var repository = new InMemoryFontRepository();
            int reads = 0;
            var source = SourceWithPassingExternalNativeChecks(repository, async (path, token) =>
            {
                reads++;
                return await File.ReadAllBytesAsync(path, token);
            });
            long firstGeneration = await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], TestContext.Current.CancellationToken);
            await repository.WriteInventoryAsync(firstGeneration, [file with { GenerationId = firstGeneration }], TestContext.Current.CancellationToken);
            FontDeepAnalysisEvidence first = await source.LoadFilesAsync(firstGeneration, [file.Id], TestContext.Current.CancellationToken);
            Assert.True(first.Faces[0].IsDeepEvidenceComplete);

            long secondGeneration = await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], TestContext.Current.CancellationToken);
            FontFaceRecord reboundFace = file.Faces[0] with
            {
                Id = 3,
                NameRecords = [.. file.Faces[0].NameRecords, new OpenTypeNameRecord(3, 1, 0x0411, 1, "Cached Family Alias")]
            };
            FontFileRecord rebound = file with { Id = 3, GenerationId = secondGeneration, Faces = [reboundFace] };
            await repository.WriteInventoryAsync(secondGeneration, [rebound], TestContext.Current.CancellationToken);
            FontDeepAnalysisEvidence second = await source.LoadFilesAsync(secondGeneration, [rebound.Id], TestContext.Current.CancellationToken);

            Assert.Equal(1, reads);
            Assert.Equal(3, Assert.Single(second.Faces).FaceId);
            Assert.Equal(3, Assert.Single(second.Files[0].Faces).FaceId);
            Assert.Equal(first.Faces[0].CharacterFingerprints.Count, second.Faces[0].CharacterFingerprints.Count);
            Assert.Contains("CACHED FAMILY ALIAS", second.Faces[0].Identity.FamilyNames);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadFiles_ShouldReuseDurableSqliteEvidenceAfterSourceRecreation()
    {
        string root = Path.Combine(Path.GetTempPath(), "ass-deep-sqlite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new FontStoragePaths(root, Path.Combine(root, "catalog.db"), Path.Combine(root, "quarantine"), Path.Combine(root, "manifests"));
            await using var database = new FontDatabase(paths);
            var repository = new SqliteFontRepository(database);
            await repository.InitializeAsync(TestContext.Current.CancellationToken);
            FontFileRecord file = await WriteDataAsync(root, "left.ttf", GeneratedAnalysisFont.Create());
            long firstGeneration = await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], TestContext.Current.CancellationToken);
            await repository.WriteInventoryAsync(firstGeneration, [file with { Id = 0, GenerationId = firstGeneration, Faces = [file.Faces[0] with { Id = 0 }] }], TestContext.Current.CancellationToken);
            FontFileRecord firstRecord = Assert.Single(await repository.LoadInventoryAsync(firstGeneration, TestContext.Current.CancellationToken));
            FontDeepAnalysisEvidence first = await SourceWithPassingExternalNativeChecks(repository).LoadFilesAsync(firstGeneration, [firstRecord.Id], TestContext.Current.CancellationToken);
            Assert.True(Assert.Single(first.Faces).IsDeepEvidenceComplete);

            long secondGeneration = await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], TestContext.Current.CancellationToken);
            await repository.WriteInventoryAsync(secondGeneration, [firstRecord with { Id = 0, GenerationId = secondGeneration, Faces = [firstRecord.Faces[0] with { Id = 0 }] }], TestContext.Current.CancellationToken);
            FontFileRecord secondRecord = Assert.Single(await repository.LoadInventoryAsync(secondGeneration, TestContext.Current.CancellationToken));
            int reads = 0;
            var recreated = SourceWithPassingExternalNativeChecks(repository, async (path, token) =>
            {
                reads++;
                return await File.ReadAllBytesAsync(path, token);
            });

            FontDeepAnalysisEvidence second = await recreated.LoadFilesAsync(secondGeneration, [secondRecord.Id], TestContext.Current.CancellationToken);

            Assert.Equal(0, reads);
            Assert.Equal(secondRecord.Faces[0].Id, Assert.Single(second.Faces).FaceId);
            Assert.True(second.Faces[0].IsDeepEvidenceComplete);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task LoadFiles_ShouldReparseWhenContentHashChangesEvenIfSizeAndTimestampMatch()
    {
        string root = Path.Combine(Path.GetTempPath(), "ass-deep-invalidate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] original = GeneratedAnalysisFont.Create();
            FontFileRecord file = await WriteDataAsync(root, "left.ttf", original);
            var repository = new InMemoryFontRepository();
            int reads = 0;
            var source = SourceWithPassingExternalNativeChecks(repository, async (path, token) =>
            {
                reads++;
                return await File.ReadAllBytesAsync(path, token);
            });
            long firstGeneration = await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], TestContext.Current.CancellationToken);
            await repository.WriteInventoryAsync(firstGeneration, [file with { GenerationId = firstGeneration }], TestContext.Current.CancellationToken);
            await source.LoadFilesAsync(firstGeneration, [file.Id], TestContext.Current.CancellationToken);

            byte[] changed = (byte[])original.Clone();
            changed[16] ^= 1;
            await File.WriteAllBytesAsync(file.Path, changed, TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(file.Path, file.ModifiedUtc.UtcDateTime);
            long secondGeneration = await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], TestContext.Current.CancellationToken);
            FontFileRecord changedRecord = file with
            {
                Id = 3,
                GenerationId = secondGeneration,
                Sha256 = SHA256.HashData(changed),
                Faces = [file.Faces[0] with { Id = 3 }]
            };
            await repository.WriteInventoryAsync(secondGeneration, [changedRecord], TestContext.Current.CancellationToken);

            FontDeepAnalysisEvidence second = await source.LoadFilesAsync(secondGeneration, [changedRecord.Id], TestContext.Current.CancellationToken);

            Assert.Equal(original.Length, changed.Length);
            Assert.Equal(file.ModifiedUtc, changedRecord.ModifiedUtc);
            Assert.Equal(2, reads);
            Assert.Equal(changedRecord.Id, Assert.Single(second.Faces).FileId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Analyze_ShouldPersistRealFaceQualityForRuntimeWithoutPreferringProtectedLowerQuality(bool sqlite)
    {
        string root = Path.Combine(Path.GetTempPath(), "ass-runtime-quality-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var paths = new FontStoragePaths(root, Path.Combine(root, "catalog.db"), Path.Combine(root, "quarantine"), Path.Combine(root, "manifests"));
            await using var database = new FontDatabase(paths);
            IFontRepository repository = sqlite ? new SqliteFontRepository(database) : new InMemoryFontRepository();
            await repository.InitializeAsync(TestContext.Current.CancellationToken);
            byte[] corrupted = GeneratedAnalysisFont.Create();
            corrupted[16] ^= 1;
            FontFileRecord left = await WriteDataAsync(root, "left.ttf", corrupted);
            FontFileRecord right = await WriteDataAsync(root, "right.ttf", GeneratedAnalysisFont.Create());
            long generation = await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], TestContext.Current.CancellationToken);
            DateTimeOffset modified = left.ModifiedUtc;
            await repository.WriteInventoryAsync(generation,
                [left with { GenerationId = generation, DirectoryPolicy = FontDirectoryPolicy.Custom | FontDirectoryPolicy.Protected }, right with { GenerationId = generation, DirectoryPolicy = FontDirectoryPolicy.Custom, ModifiedUtc = modified }], TestContext.Current.CancellationToken);
            var analyzer = new FontDuplicateAnalyzer(new PhysicalFontAnalysisEvidenceSource(repository), new FontRelationshipClassifier(), new FontRetentionSelector(), TimeProvider.System);

            AnalysisBatch analysis = await analyzer.AnalyzeAsync(generation, null, TestContext.Current.CancellationToken);
            analysis = await repository.PublishAnalysisAsync(analysis, TestContext.Current.CancellationToken);
            IReadOnlyList<RuntimeFontFace> faces = await repository.LoadPublishedRuntimeFacesAsync(TestContext.Current.CancellationToken);
            RuntimeFontFace lower = faces.Single(face => face.Path == left.Path);
            RuntimeFontFace higher = faces.Single(face => face.Path == right.Path);
            AnalysisBatch loaded = (await repository.GetAnalysisBatchAsync(analysis.Id, TestContext.Current.CancellationToken))!;

            Assert.Equal(2, loaded.FaceScores.Count);
            Assert.Contains("\"freetype\":", loaded.FaceScores[0].VerificationJson, StringComparison.Ordinal);
            Assert.Equal(4, higher.RetentionScore - lower.RetentionScore); // Verified checksum component only.
            Assert.False(lower.IsRuntimePreferred);
            Assert.Equal(0, analysis.ReclaimableBytes); // Protected source remains even though quality is lower.
            var request = new FontDescriptor(lower.FamilyName, null, false, false);
            Assert.Equal(right.Path, new RuntimeFontIndex(faces).Find(request)!.Path);
            Assert.Equal(left.Path, new RuntimeFontIndex([lower, higher with { Weight = 700 }]).Find(request)!.Path);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("valid", true, true)]
    [InlineData("checksum", false, true)]
    [InlineData("embedding", true, false)]
    [InlineData("capability", true, true)]
    public async Task LoadFiles_ShouldCreditOnlyVerifiedQuality(string mutation, bool checksumCredit, bool embeddingCredit)
    {
        string root = Path.Combine(Path.GetTempPath(), "ass-quality-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] data = GeneratedAnalysisFont.Create(modify: tables =>
            {
                if (mutation == "embedding")
                {
                    BinaryPrimitives.WriteUInt16BigEndian(tables["OS/2"].AsSpan(8), 2);
                }
                else if (mutation == "capability")
                {
                    tables["GSUB"] = new byte[10];
                    tables["COLR"] = new byte[14];
                    tables["CPAL"] = new byte[12];
                    tables["vhea"] = new byte[36];
                    tables["vmtx"] = new byte[20];
                }
            });
            if (mutation == "checksum")
            {
                data[16] ^= 1; // First directory checksum only; source table bytes remain valid.
            }

            FontFileRecord file = await WriteDataAsync(root, "left.ttf", data);
            var repository = new InMemoryFontRepository();
            long generation = await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], TestContext.Current.CancellationToken);
            await repository.WriteInventoryAsync(generation, [file with { GenerationId = generation }], TestContext.Current.CancellationToken);
            await repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = generation, Status = AnalysisBatchStatus.Complete, SummaryJson = "{\"faceScores\":[]}" }, TestContext.Current.CancellationToken);

            FontDeepAnalysisEvidence deep = await new PhysicalFontAnalysisEvidenceSource(repository).LoadFilesAsync(generation, [1], TestContext.Current.CancellationToken);
            FontDeepAnalysisEvidence restored = FontDeepEvidenceJson.Deserialize(FontDeepEvidenceJson.Serialize(deep));
            Assert.Equal(deep.Faces[0].CharacterFingerprints.Count, restored.Faces[0].CharacterFingerprints.Count);
            Assert.Equal(deep.Files[0].Faces[0].Quality.Verification.Count, restored.Files[0].Faces[0].Quality.Verification.Count);
            IReadOnlySet<FontQualitySignal> signals = deep.Files[0].Faces[0].Quality.Signals;
            Assert.DoesNotContain("signature", deep.Files[0].Faces[0].Quality.Verification.Keys);

            Assert.Equal(mutation == "capability" ? FontVerificationStatus.Failed : FontVerificationStatus.Passed, deep.Files[0].Faces[0].Quality.Verification["skia"]);
            if (mutation == "valid")
            {
                Assert.Equal(FontVerificationStatus.Passed, deep.Files[0].Faces[0].Quality.Verification["harfbuzz"]);
            }

            Assert.Equal(checksumCredit, signals.Contains(FontQualitySignal.ValidChecksums));
            Assert.Equal(embeddingCredit, signals.Contains(FontQualitySignal.EmbeddingRights));
            bool allNativeLoadersPassed = new[] { "skia", "harfbuzz", "freetype", "libass" }
                .All(name => deep.Files[0].Faces[0].Quality.Verification[name] == FontVerificationStatus.Passed);
            Assert.Equal(allNativeLoadersPassed, signals.Contains(FontQualitySignal.StableNativeLoaders));
            Assert.DoesNotContain(FontQualitySignal.LayoutComplete, signals);
            Assert.DoesNotContain(FontQualitySignal.ColorGlyphsComplete, signals);
            Assert.DoesNotContain(FontQualitySignal.VerticalMetricsComplete, signals);
            Assert.DoesNotContain(FontQualitySignal.VariationAxesComplete, signals);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("head", 18, 1024)]
    [InlineData("head", 36, 1)]
    [InlineData("OS/2", 62, 1)]
    [InlineData("OS/2", 8, 2)]
    [InlineData("OS/2", 4, 500)]
    [InlineData("OS/2", 6, 6)]
    public async Task Classify_ShouldNotTreatFunctionalFieldsAsMetadata(string table, int offset, ushort value)
    {
        string root = Path.Combine(Path.GetTempPath(), "ass-functional-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            FontFileRecord left = await WriteDataAsync(root, "left.ttf", GeneratedAnalysisFont.Create());
            FontFileRecord right = await WriteDataAsync(root, "right.ttf", GeneratedAnalysisFont.Create(modify: tables => BinaryPrimitives.WriteUInt16BigEndian(tables[table].AsSpan(offset), value)));
            var repository = new InMemoryFontRepository();
            long generation = await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], TestContext.Current.CancellationToken);
            await repository.WriteInventoryAsync(generation, [left with { GenerationId = generation }, right with { GenerationId = generation }], TestContext.Current.CancellationToken);
            await repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = generation, Status = AnalysisBatchStatus.Complete, SummaryJson = "{\"faceScores\":[]}" }, TestContext.Current.CancellationToken);
            FontDeepAnalysisEvidence deep = await SourceWithPassingExternalNativeChecks(repository).LoadFilesAsync(generation, [1, 2], TestContext.Current.CancellationToken);

            FontRelationshipResult result = new FontRelationshipClassifier().Classify(deep.Faces[0], deep.Faces[1]);

            Assert.NotEqual(FontRelationshipKind.MetadataOnlyDifference, result.Kind);
            Assert.False(result.LeftCanReplaceRight);
            Assert.False(result.RightCanReplaceLeft);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("metadata", FontRelationshipKind.MetadataOnlyDifference)]
    [InlineData("hinting", FontRelationshipKind.HintingOnlyDifference)]
    [InlineData("mapping", FontRelationshipKind.RenderingAndLayoutEquivalent)]
    public async Task Classify_ShouldCanonicalizeRealFieldLevelSemantics(string difference, FontRelationshipKind expected)
    {
        string root = Path.Combine(Path.GetTempPath(), "ass-semantic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] original = GeneratedAnalysisFont.Create();
            byte[] changed = GeneratedAnalysisFont.Create(modify: tables =>
            {
                if (difference == "metadata")
                {
                    tables["head"][20] = 1;
                    "TEST"u8.CopyTo(tables["OS/2"].AsSpan(58, 4));
                }
                else if (difference == "hinting")
                {
                    byte[] glyphs = new byte[70];
                    byte[] offsets = new byte[24];
                    for (int glyph = 0; glyph < 5; glyph++)
                    {
                        BinaryPrimitives.WriteUInt32BigEndian(offsets.AsSpan(glyph * 4), (uint)(glyph * 14));
                        BinaryPrimitives.WriteUInt16BigEndian(glyphs.AsSpan((glyph * 14) + 10), glyph == 2 ? (ushort)1 : (ushort)0);
                    }

                    BinaryPrimitives.WriteUInt32BigEndian(offsets.AsSpan(20), 70);
                    tables["glyf"] = glyphs;
                    tables["loca"] = offsets;
                    BinaryPrimitives.WriteUInt16BigEndian(tables["maxp"].AsSpan(26), 1);
                }
                else
                {
                    byte[] cmap = OpenTypeFontReader.Read(TestFontBuilder.Sfnt().WithCmapFormat4(32, 35).Build()).Faces[0].Tables[OpenTypeTag.FromString("cmap")].Data.ToArray();
                    BinaryPrimitives.WriteInt16BigEndian(cmap.AsSpan(36), -31); // 32=>1 ... 35=>4.
                    tables["cmap"] = cmap;
                }
            });
            FontFileRecord left = await WriteDataAsync(root, "left.ttf", original);
            FontFileRecord right = await WriteDataAsync(root, "right.ttf", changed);
            var repository = new InMemoryFontRepository();
            long generation = await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], TestContext.Current.CancellationToken);
            await repository.WriteInventoryAsync(generation, [left with { GenerationId = generation }, right with { GenerationId = generation }], TestContext.Current.CancellationToken);
            await repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = generation, Status = AnalysisBatchStatus.Complete, SummaryJson = "{\"faceScores\":[]}" }, TestContext.Current.CancellationToken);
            FontDeepAnalysisEvidence deep = await SourceWithPassingExternalNativeChecks(repository).LoadFilesAsync(generation, [1, 2], TestContext.Current.CancellationToken);
            Assert.All(deep.Faces, face => Assert.True(face.IsDeepEvidenceComplete, face.ClassificationError));

            FontRelationshipResult result = new FontRelationshipClassifier().Classify(deep.Faces[0], deep.Faces[1]);

            Assert.Equal(expected, result.Kind);
            if (difference != "hinting")
            {
                Assert.True(result.LeftCanReplaceRight);
                Assert.True(result.RightCanReplaceLeft);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(2, true)]
    public async Task CoverageSuperset_ShouldPreserveActualVariationGlyphBehavior(ushort replacementGlyph, bool mayReplace)
    {
        string root = Path.Combine(Path.GetTempPath(), "ass-uvs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var repository = new InMemoryFontRepository();
            long generation = await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], TestContext.Current.CancellationToken);
            FontFileRecord left = await WriteFileAsync(root, "left.ttf", true, replacementGlyph);
            FontFileRecord right = await WriteFileAsync(root, "right.ttf", false, 2);
            await repository.WriteInventoryAsync(generation, [left with { GenerationId = generation }, right with { GenerationId = generation }], TestContext.Current.CancellationToken);
            await repository.PublishAnalysisAsync(new AnalysisBatch { GenerationId = generation, Status = AnalysisBatchStatus.Complete, SummaryJson = "{\"faceScores\":[]}" }, TestContext.Current.CancellationToken);
            var source = SourceWithPassingExternalNativeChecks(repository);
            FontDeepAnalysisEvidence deep = await source.LoadFilesAsync(generation, repository.PublishedFiles.Select(file => file.Id).ToArray(), TestContext.Current.CancellationToken);
            FontFaceEvidence replacement = deep.Faces.Single(face => face.FileId == repository.PublishedFiles.Single(file => file.Path == left.Path).Id);
            FontFaceEvidence removed = deep.Faces.Single(face => face.FileId == repository.PublishedFiles.Single(file => file.Path == right.Path).Id);

            Assert.True(replacement.IsDeepEvidenceComplete, replacement.ClassificationError);
            Assert.True(removed.IsDeepEvidenceComplete, removed.ClassificationError);
            FontRelationshipResult result = new FontRelationshipClassifier().Classify(replacement, removed);

            if (mayReplace)
            {
                Assert.Equal(FontRelationshipKind.CoverageSuperset, result.Kind);
            }

            Assert.Equal(mayReplace, result.LeftCanReplaceRight);
            Assert.False(result.RightCanReplaceLeft);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<FontDeepAnalysisEvidence> CompleteCacheFixtureAsync(FontFileRecord file)
    {
        var repository = new InMemoryFontRepository();
        long generation = await repository.BeginGenerationAsync(Jellyfin.Plugin.AssSubsetter.Fonts.Inventory.FontAnalysisInputSnapshot.Empty, [], TestContext.Current.CancellationToken);
        await repository.WriteInventoryAsync(generation, [file with { GenerationId = generation }], TestContext.Current.CancellationToken);
        FontDeepAnalysisEvidence deep = await new PhysicalFontAnalysisEvidenceSource(repository).LoadFilesAsync(generation, [file.Id], TestContext.Current.CancellationToken);
        // Emulate durable complete evidence whose verification can be independently inconsistent.
        var verification = new Dictionary<string, FontVerificationStatus>(StringComparer.Ordinal)
        {
            ["checksums"] = FontVerificationStatus.Passed,
            ["embeddingRights"] = FontVerificationStatus.Passed,
            ["skia"] = FontVerificationStatus.Passed,
            ["harfbuzz"] = FontVerificationStatus.Passed,
            ["freetype"] = FontVerificationStatus.Passed,
            ["libass"] = FontVerificationStatus.Passed,
            ["layout"] = FontVerificationStatus.Passed,
            ["vertical"] = FontVerificationStatus.Passed,
            ["color"] = FontVerificationStatus.Passed,
            ["axes"] = FontVerificationStatus.Passed
        };
        return deep with
        {
            Faces = [deep.Faces[0] with { IsClassifiable = true, IsDeepEvidenceComplete = true, ClassificationError = null }],
            Files = [deep.Files[0] with
            {
                Faces = [deep.Files[0].Faces[0] with
                {
                    IsClassifiable = true,
                    Quality = deep.Files[0].Faces[0].Quality with { Verification = verification }
                }]
            }]
        };
    }

    private static Mock<IFontRepository> CachedRepository(FontFileRecord file, string? payload)
    {
        var repository = new Mock<IFontRepository>();
        repository.Setup(value => value.LoadInventoryAsync(1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { file with { GenerationId = 1 } });
        repository.Setup(value => value.LoadReusableDeepEvidenceAsync(It.IsAny<FontFileRecord>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(payload);
        return repository;
    }

    private static PhysicalFontAnalysisEvidenceSource SourceWithPassingExternalNativeChecks(
        IFontRepository repository,
        Func<string, CancellationToken, Task<byte[]>>? readFile = null)
        => new(repository, readFile ?? File.ReadAllBytesAsync,
            (_, _, _) => FontVerificationStatus.Passed,
            (_, _) => FontVerificationStatus.Passed);

    private static async Task<FontFileRecord> WriteFileAsync(string root, string name, bool extraAlias, ushort variationGlyph)
    {
        byte[] data = GeneratedAnalysisFont.Create(Cmap(extraAlias, variationGlyph));
        return await WriteDataAsync(root, name, data);
    }

    private static async Task<FontFileRecord> WriteDataAsync(string root, string name, byte[] data)
    {
        string path = Path.Combine(root, name);
        await File.WriteAllBytesAsync(path, data, TestContext.Current.CancellationToken);
        OpenTypeFace face = OpenTypeFontReader.Read(data).Faces[0];
        InspectedFontFace inspected = FontFaceInspector.Inspect(face);
        return new FontFileRecord
        {
            Id = name == "left.ttf" ? 1 : 2,
            Path = path,
            NormalizedPath = path.ToUpperInvariant(),
            PhysicalIdentity = name,
            Size = data.Length,
            ModifiedUtc = File.GetLastWriteTimeUtc(path),
            Sha256 = SHA256.HashData(data),
            Health = FontFileHealth.Healthy,
            Faces = [new FontFaceRecord
            {
                Id = name == "left.ttf" ? 1 : 2,
                FaceIndex = 0,
                StrictFingerprint = FontFingerprintBuilder.BuildFace(face).StrictFingerprint,
                FamilyName = inspected.FamilyName,
                StyleName = inspected.StyleName,
                Names = inspected.Names,
                NameRecords = inspected.NameRecords,
                Technology = inspected.Technology,
                UnitsPerEm = inspected.UnitsPerEm,
                Weight = inspected.Weight,
                Width = inspected.Width
            }]
        };
    }

    private static byte[] Cmap(bool extraAlias, ushort variationGlyph)
    {
        // Literal format 12 ordinary A=>2 (and optional B=>3), then format 14 A+FE0F=>glyph.
        byte[] data = new byte[78];
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), 2);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(6), 10);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), 20);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(14), 5);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16), 48);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(20), 12);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(24), 28);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(32), 1);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(36), 0x41);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(40), extraAlias ? 0x42U : 0x41U);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(44), 2);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(48), 14);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(50), 30);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(54), 1);
        data[59] = 0xfe;
        data[60] = 0x0f;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(65), 21);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(69), 1);
        data[75] = 0x41;
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(76), variationGlyph);
        return data;
    }
}
