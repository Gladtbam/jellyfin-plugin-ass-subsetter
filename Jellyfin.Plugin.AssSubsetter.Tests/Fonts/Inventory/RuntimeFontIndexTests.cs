using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Models;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Inventory;

public sealed class RuntimeFontIndexTests
{
    [Fact]
    public void Find_ShouldOrderStyleBeforeRetentionScore()
    {
        var index = new RuntimeFontIndex(
        [
            Face(1, italic: false, weight: 400, retention: 999),
            Face(2, italic: true, weight: 400, retention: 1)
        ]);

        RuntimeFontFace? result = index.Find(new FontDescriptor("Example", 400, false, true));

        Assert.NotNull(result);
        Assert.Equal(2, result.FaceId);
    }

    [Fact]
    public void Find_ShouldPreferExactAliasOverLooseCandidate()
    {
        var index = new RuntimeFontIndex(
        [
            Face(1, name: "Example Sans Extended"),
            Face(2, name: "Example Sans")
        ]);

        RuntimeFontFace? result = index.Find(new FontDescriptor("Example Sans", 400, false, false));

        Assert.NotNull(result);
        Assert.Equal(2, result.FaceId);
    }

    [Fact]
    public void Find_ShouldTreatPreNormalizedAndCombiningAliasesIdentically()
    {
        var index = new RuntimeFontIndex([Face(1, name: "Cafe\u0301")]);

        RuntimeFontFace? result = index.Find(new FontDescriptor("Café", 400, false, false));

        Assert.NotNull(result);
        Assert.Equal(1, result.FaceId);
    }

    [Fact]
    public void Find_ShouldReturnTheOnlyExactFaceDespiteStyleMismatch()
    {
        var index = new RuntimeFontIndex([Face(1, italic: false, weight: 400)]);

        RuntimeFontFace? result = index.Find(new FontDescriptor("Example", 700, true, true));

        Assert.NotNull(result);
        Assert.Equal(1, result.FaceId);
    }

    [Fact]
    public void Find_ShouldPreferFamilyAliasOverAnotherFacesFullName()
    {
        var family = new RuntimeFontFace(1, "/fonts/family.ttf", 0, "Example", "Bold", ["Example"], 700, 5, 0, false, false, "TrueType", 0, false);
        var collidingFullName = new RuntimeFontFace(2, "/fonts/full.ttf", 0, "Other", "Regular", ["Example"], 400, 5, 0, false, false, "TrueType", 0, false);
        var index = new RuntimeFontIndex([family, collidingFullName]);

        RuntimeFontFace? result = index.Find(new FontDescriptor("Example", 400, false, false));

        Assert.Equal(1, result?.FaceId);
    }

    [Fact]
    public void Find_ShouldUseFullNameCandidateWhenFamilyCandidateIsInvalidated()
    {
        var family = new RuntimeFontFace(1, "/fonts/removed.ttf", 0, "Example", "Regular", ["Example"], 400, 5, 0, false, false, "TrueType", 0, false);
        var fullName = new RuntimeFontFace(2, "/fonts/available.ttf", 0, "Other", "Regular", ["Example"], 400, 5, 0, false, false, "TrueType", 0, false);
        var index = new RuntimeFontIndex([family, fullName]);

        RuntimeFontFace? result = index.Find(new FontDescriptor("Example", 400, false, false), face => face.FaceId != 1);

        Assert.Equal(2, result?.FaceId);
    }

    [Fact]
    public void Find_ShouldRankQualityBeforePreferredDirectory()
    {
        var quality = new RuntimeFontFace(1, "/fonts/quality.ttf", 0, "Example", "Regular", ["Example"], 400, 5, 0, false, false, "TrueType", 70, false);
        var preferred = new RuntimeFontFace(2, "/fonts/preferred.ttf", 0, "Example", "Regular", ["Example"], 400, 5, 0, false, false, "TrueType", 10, true);
        var index = new RuntimeFontIndex([quality, preferred]);

        RuntimeFontFace? result = index.Find(new FontDescriptor("Example", 400, false, false));

        Assert.Equal(1, result?.FaceId);
    }

    [Fact]
    public void Find_ShouldPreferItalicStyleLabelWhenMetadataTies()
    {
        var oblique = new RuntimeFontFace(1, "/fonts/a-oblique.ttf", 0, "Example", "Oblique", ["Example"], 400, 5, 1, true, false, "TrueType", 0, false);
        var italic = new RuntimeFontFace(2, "/fonts/z-italic.ttf", 0, "Example", "Italic", ["Example"], 400, 5, 1, true, false, "TrueType", 0, false);
        var index = new RuntimeFontIndex([oblique, italic]);

        RuntimeFontFace? result = index.Find(new FontDescriptor("Example", 400, false, true));

        Assert.Equal(2, result?.FaceId);
    }

    [Fact]
    public void Find_ShouldFilterLooseCandidatesBeforeBoundingThem()
    {
        RuntimeFontFace[] decoys = Enumerable.Range(0, 256)
            .Select(i => new RuntimeFontFace(i + 1, $"/fonts/{i:D3}.ttf", 0, $"Example {i:D3}", "Regular", [$"Example {i:D3}"], 400, 5, 0, false, false, "TrueType", 0, false))
            .ToArray();
        var target = new RuntimeFontFace(1000, "/fonts/target.ttf", 0, "Example Sans", "Regular", ["Example Sans"], 400, 5, 0, false, false, "TrueType", 0, false);
        var index = new RuntimeFontIndex([.. decoys, target]);

        RuntimeFontFace? result = index.Find(new FontDescriptor("Example Sans Extended", 400, false, false));

        Assert.Equal(1000, result?.FaceId);
    }

    [Fact]
    public void Find_ShouldMatchWwsFamilyNameFromTypedEvidence()
    {
        var face = new RuntimeFontFace(
            1, "/fonts/wws.ttf", 0, "Preferred Family", "Regular", ["Preferred Family"],
            400, 5, 0, false, false, "TrueType", 0, false)
        {
            NameRecords = [new OpenTypeNameRecord(3, 1, 1033, 21, "WWS Family")]
        };
        var index = new RuntimeFontIndex([face]);

        RuntimeFontFace? result = index.Find(new FontDescriptor("WWS Family", 400, false, false));

        Assert.Equal(1, result?.FaceId);
    }

    [Fact]
    public void Find_ShouldPreferFaceCoveringAllRequiredSubtitleCharacters()
    {
        var partial = Face(1, retention: 90) with { UnicodeCoverage = [new UnicodeRange('A', 'A')] };
        var complete = Face(2, retention: 10) with { UnicodeCoverage = [new UnicodeRange('A', 'B')] };
        var index = new RuntimeFontIndex([partial, complete]);

        RuntimeFontFace? result = index.Find(new FontDescriptor("Example", 400, false, false), requiredCharacters: ['A', 'B']);

        Assert.Equal(2, result?.FaceId);
    }

    [Fact]
    public void Find_ShouldKeepExistingRankingWhenCoverageIsUnavailable()
    {
        var unknown = Face(1, retention: 90);
        var partial = Face(2, retention: 10) with { UnicodeCoverage = [new UnicodeRange('A', 'A')] };
        var index = new RuntimeFontIndex([unknown, partial]);

        RuntimeFontFace? result = index.Find(new FontDescriptor("Example", 400, false, false), requiredCharacters: ['A', 'B']);

        Assert.Equal(1, result?.FaceId);
    }

    private static RuntimeFontFace Face(
        long id,
        string name = "Example",
        bool italic = false,
        int weight = 400,
        int retention = 0)
    {
        return new RuntimeFontFace(id, $"/fonts/{id}.ttf", 0, name, "Regular", [name], weight, 5, italic ? 1 : 0, italic, false, "TrueType", retention, false);
    }
}
