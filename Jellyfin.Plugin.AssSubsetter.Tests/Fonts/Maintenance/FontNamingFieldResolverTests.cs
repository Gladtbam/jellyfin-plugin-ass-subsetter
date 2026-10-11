using System.Text.Json;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance.Naming;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Maintenance;

public sealed class FontNamingFieldResolverTests
{
    [Theory]
    [InlineData("Typed", "Bold", "Legacy", "Regular", "Typed", "Bold", "typographic-pair")]
    [InlineData("Typed", null, "Legacy", "Regular", "Legacy", "Regular", "legacy-pair")]
    [InlineData("Typed", null, "Legacy", null, "Typed", "", "family-only")]
    [InlineData(null, "Bold", "Legacy", null, "Legacy", "", "family-only")]
    [InlineData(null, "Bold", null, "Regular", "", "", "family-only")]
    public void CompletePairsDoNotMixSources(string? typedFamily, string? typedStyle, string? legacyFamily, string? legacyStyle, string family, string style, string selection)
    {
        (ushort Id, string? Value)[] names = [(16, typedFamily), (17, typedStyle), (1, legacyFamily), (2, legacyStyle)];
        var fields = Resolve(names.Where(name => name.Value is not null).Select(name => (name.Id, name.Value!)).ToArray());
        Assert.Equal(family, fields.Get("family").Text);
        Assert.Equal(style, fields.Get("style").Text);
        Assert.Equal(style.Length == 0 ? "" : "-" + style, fields.Get("styleSuffix").Text);
        Assert.Contains(fields.Selections, item => item.Kind == selection && item.FaceIndex == 0);
        Assert.Equal(family.Length == 0, fields.Get("family").BlocksWhenReferenced);
    }

    [Fact]
    public void FamilyOnlyDoesNotInferRegular()
    {
        var fields = Resolve((1, "Usable"));
        Assert.Equal("Usable", fields.Get("family").Text);
        Assert.Equal("", fields.Get("style").Text);
        Assert.Equal("", fields.Get("styleSuffix").Text);
        Assert.False(fields.Get("styleSuffix").BlocksWhenReferenced);
        Assert.NotNull(fields.Get("style").MissingReason);
    }

    [Theory]
    [InlineData("fullName", true)]
    [InlineData("postscript", true)]
    [InlineData("legacyFamily", true)]
    [InlineData("typographicFamily", true)]
    [InlineData("manufacturer", false)]
    [InlineData("version", false)]
    [InlineData("legacyStyle", false)]
    [InlineData("typographicStyle", false)]
    public void ReferencedMissingPolicyIsExplicit(string field, bool blocks)
    {
        var value = Resolve().Get(field);
        Assert.Equal("", value.Text);
        Assert.Equal("missing-field", value.MissingReason);
        Assert.Equal(blocks, value.BlocksWhenReferenced);
    }

    [Fact]
    public void MixedCollectionSortsFamiliesAndRequiresCommonValues()
    {
        var beta = FontNamingTestData.Face(1, 1, (1, "Beta"), (2, "Bold"), (4, "Beta Bold"), (8, "Maker")) with { Version = "Version 1.004" };
        var alpha = FontNamingTestData.Face(0, 0, (16, "Alpha"), (17, "Bold"), (4, "Alpha Bold"), (8, "Maker")) with { Version = "1.004" };
        var file = FontNamingTestData.File("source.TTC", beta, alpha);
        string before = JsonSerializer.Serialize(file);
        var fields = FontNamingFieldResolver.Resolve(file);
        Assert.Equal("Alpha+Beta", fields.Get("family").Text);
        Assert.Equal("Bold", fields.Get("style").Text);
        Assert.Equal("Maker", fields.Get("manufacturer").Text);
        Assert.Equal("1.004", fields.Get("version").Text);
        Assert.Equal("-v1.004", fields.Get("versionSuffix").Text);
        Assert.Equal("-MixedCollection", fields.Get("collectionSuffix").Text);
        Assert.Equal("2", fields.Get("faceCount").Text);
        Assert.Equal("source", fields.Get("originalName").Text);
        Assert.Equal("5c85955f", fields.Get("fingerprint").Text);
        Assert.True(fields.Get("fullName").BlocksWhenReferenced);
        Assert.Equal("inconsistent-face-field", fields.Get("fullName").MissingReason);
        Assert.Equal("Alpha+Beta", FontNamingFieldResolver.Resolve(file with { Faces = [alpha, beta] }).Get("family").Text);
        Assert.Equal("5c85955f", FontNamingFieldResolver.Resolve(file with { Faces = [alpha, beta] }).Get("fingerprint").Text);
        Assert.Equal(before, JsonSerializer.Serialize(file));
        Assert.Contains(fields.Get("family").Evidence, item => item.NameId == 16 && item.FaceIndex == 0);
        Assert.Contains(fields.Get("family").Evidence, item => item.NameId == 1 && item.FaceIndex == 1);
    }

    [Fact]
    public void CollectionDoesNotChooseFirstOrMaximumOptionalValues()
    {
        var first = FontNamingTestData.Face(0, 0, (1, "Family"), (2, "Bold"), (8, "Maker")) with { Version = "1.00" };
        var second = FontNamingTestData.Face(1, 1, (1, "Family"), (2, "Regular"), (8, "Other")) with { Version = "1.0" };
        var fields = FontNamingFieldResolver.Resolve(FontNamingTestData.File("source.ttc", first, second));
        Assert.Equal("Family", fields.Get("family").Text);
        Assert.Equal("-Collection", fields.Get("collectionSuffix").Text);
        foreach (string name in new[] { "style", "styleSuffix", "manufacturer", "version", "versionSuffix" })
        {
            Assert.Equal("", fields.Get(name).Text);
            Assert.Equal("inconsistent-face-field", fields.Get(name).MissingReason);
            Assert.False(fields.Get(name).BlocksWhenReferenced);
        }
    }

    [Fact]
    public void RawNamesRemainRaw()
    {
        var fields = Resolve((1, "Legacy"), (2, "Regular"), (16, "Typed"), (17, "Bold"), (4, "Full Display"), (6, "PostScript"), (8, "Maker"));
        Assert.Equal("Typed", fields.Get("family").Text);
        Assert.Equal("Bold", fields.Get("style").Text);
        Assert.Equal("Legacy", fields.Get("legacyFamily").Text);
        Assert.Equal("Regular", fields.Get("legacyStyle").Text);
        Assert.Equal("Typed", fields.Get("typographicFamily").Text);
        Assert.Equal("Bold", fields.Get("typographicStyle").Text);
        Assert.Equal("Full Display", fields.Get("fullName").Text);
        Assert.Equal("PostScript", fields.Get("postscript").Text);
        Assert.Equal("Maker", fields.Get("manufacturer").Text);
    }

    [Fact]
    public void InvalidOptionalUnicodeDoesNotHideFamily()
    {
        var fields = Resolve((1, "Usable"), (2, "\ud800"));
        Assert.Equal("Usable", fields.Get("family").Text);
        Assert.False(fields.Get("family").BlocksWhenReferenced);
        Assert.Equal("invalid-unicode", fields.Get("style").MissingReason);
        Assert.Contains(fields.Get("style").Evidence, item => item.FaceIndex == 0 && item.NameId == 2 && item.MissingReason == "invalid-unicode");
    }

    [Theory]
    [InlineData("  ", "blank-field")]
    [InlineData("?<>*", "sanitized-empty")]
    public void UnusableRequiredTextKeepsSpecificReason(string text, string reason)
    {
        var value = Resolve((1, text)).Get("family");
        Assert.True(value.BlocksWhenReferenced);
        Assert.Equal(reason, value.MissingReason);
    }

    [Fact]
    public void InvalidRequiredUnicodeKeepsSpecificReason()
    {
        var value = Resolve((1, "\uD800")).Get("family");
        Assert.True(value.BlocksWhenReferenced);
        Assert.Equal("invalid-unicode", value.MissingReason);
    }
    [Fact]
    public void SafeUnicodeIsNormalizedWithoutBreakingDevicePrefix()
    {
        var fields = Resolve((1, " Cafe\u0301 "), (2, "Bold/Italic"), (8, "CON"));
        Assert.Equal("Café", fields.Get("family").Text);
        Assert.Equal("Bold-Italic", fields.Get("style").Text);
        Assert.Equal("_CON", fields.Get("manufacturer").Text);
        Assert.Equal("_CON", FontNamingComponent.Normalize(FontNamingComponent.Normalize("CON")));
    }

    [Theory]
    [InlineData(".ttc")]
    [InlineData(".OTC")]
    public void SingleFaceCollectionHasAutomaticSuffix(string extension)
    {
        var fields = FontNamingFieldResolver.Resolve(FontNamingTestData.File("source" + extension, FontNamingTestData.Face(names: [(1, "Family"), (2, "Regular")])));
        Assert.Equal("-Collection", fields.Get("collectionSuffix").Text);
        Assert.Equal("1", fields.Get("faceCount").Text);
    }

    [Fact]
    public void CollectionFamilyDependencyCannotHideMissingFace()
    {
        var fields = FontNamingFieldResolver.Resolve(FontNamingTestData.File("source.ttc", FontNamingTestData.Face(0, 0, (1, "Family")), FontNamingTestData.Face(1, 1)));
        Assert.True(fields.Get("family").BlocksWhenReferenced);
        Assert.True(fields.Get("collectionSuffix").BlocksWhenReferenced);
        Assert.Contains(fields.Get("family").Evidence, item => item.FaceIndex == 1 && item.MissingReason is not null);
    }

    [Fact]
    public void FingerprintRequiresEveryCompleteFaceDigest()
    {
        var valid = FontNamingTestData.Face(0, 0, (1, "Family"));
        var invalid = FontNamingTestData.Face(1, 1, (1, "Family")) with { StrictFingerprint = new byte[4] };
        var value = FontNamingFieldResolver.Resolve(FontNamingTestData.File("source.ttc", valid, invalid)).Get("fingerprint");
        Assert.True(value.BlocksWhenReferenced);
        Assert.Equal("invalid-fingerprint", value.MissingReason);
        Assert.Equal("", value.Text);
    }

    [Fact]
    public void NoFacesCannotSupplyRequiredIdentity()
    {
        var fields = FontNamingFieldResolver.Resolve(FontNamingTestData.File("source.ttf"));
        Assert.True(fields.Get("family").BlocksWhenReferenced);
        Assert.True(fields.Get("faceCount").BlocksWhenReferenced);
        Assert.Equal("no-faces", fields.Get("faceCount").MissingReason);
    }

    [Fact]
    public void SingleFontCollectionSuffixIsIntentionallyEmpty()
    {
        var value = Resolve((1, "Family")).Get("collectionSuffix");
        Assert.Equal("", value.Text);
        Assert.Null(value.MissingReason);
        Assert.False(value.BlocksWhenReferenced);
    }

    [Fact]
    public void PreferredLanguageSelectionRemainsDeterministic()
    {
        var face = FontNamingTestData.Face() with { NameRecords = [new OpenTypeNameRecord(0, 3, 0, 1, "Unicode"), new OpenTypeNameRecord(3, 1, 1033, 1, "English")] };
        Assert.Equal("English", FontNamingFieldResolver.Resolve(FontNamingTestData.File("source.ttf", face)).Get("family").Text);
    }

    private static FontNamingFields Resolve(params (ushort Id, string Value)[] names)
        => FontNamingFieldResolver.Resolve(FontNamingTestData.File("source.otf", FontNamingTestData.Face(names: names)));
}
