using System.Globalization;
using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance.Naming;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Maintenance;

public class FontNamingTemplateTests
{
    [Fact]
    public void DefaultTemplateMatchesApprovedExamples()
    {
        var bold = FontNamingTestData.Face(names: [(1, "Example Sans"), (2, "Bold")]) with { Version = "1.004" };
        var regular = FontNamingTestData.Face(1, 1, (1, "Example Sans"), (2, "Regular")) with { Version = "1.004" };
        var familyOnly = FontNamingTestData.Face(names: [(1, "Example Sans")]) with { Version = "1.004" };
        (FontFileRecord File, string Expected)[] examples =
        [
            (FontNamingTestData.File("a.otf", bold), "Example Sans-Bold-v1.004"),
            (FontNamingTestData.File("a.otf", bold with { Version = null }), "Example Sans-Bold"),
            (FontNamingTestData.File("a.otf", familyOnly), "Example Sans-v1.004"),
            (FontNamingTestData.File("a.ttc", bold, regular), "Example Sans-Collection-v1.004"),
            (FontNamingTestData.File("a.ttc", FontNamingTestData.Face(0, 0, (1, "Beta"), (2, "Bold")), FontNamingTestData.Face(1, 1, (1, "Alpha"), (2, "Regular"))), "Alpha+Beta-MixedCollection"),
            (FontNamingTestData.File("a.ttc", regular with { Version = "1.0" }), "Example Sans-Regular-Collection-v1.0")
        ];
        foreach (var example in examples)
        {
            var result = Expand(FontNamingRules.DefaultFileNameTemplate, example.File);
            Assert.Null(result.SkipReason);
            Assert.Equal(example.Expected, result.Components.Single());
        }
        Assert.Equal("{family}{styleSuffix}{collectionSuffix}{versionSuffix}", new PluginConfiguration().FontFileNameTemplate);
        Assert.Equal(2, new FontNamingRules(FontNamingRules.DefaultFileNameTemplate, "", false).FormatVersion);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("resolvedName")]
    [InlineData("resolvedFamily")]
    [InlineData("resolvedStyle")]
    [InlineData("container")]
    [InlineData("isCollection")]
    [InlineData("extension")]
    [InlineData("Family")]
    public void OldFieldsAreRejectedWithPosition(string field)
    {
        var error = Assert.Single(Compile("  {" + field + "} ").Errors);
        Assert.Equal("unknown-field", error.Code);
        Assert.Equal(3, error.Offset);
    }

    [Theory]
    [InlineData("family ?? originalName")]
    [InlineData("true ? family : style")]
    [InlineData("!isCollection")]
    [InlineData("true && false")]
    [InlineData("true || false")]
    [InlineData("(let a = family; a)")]
    [InlineData("faces(family)")]
    [InlineData("has(family)")]
    [InlineData("require(family)")]
    [InlineData("requireAll(family)")]
    [InlineData("first(family)")]
    [InlineData("allEqual(family)")]
    [InlineData("clean(family)")]
    [InlineData("compact(family)")]
    [InlineData("endsWithIgnoreCase(family, style)")]
    [InlineData("upper(lower(family))")]
    [InlineData("upper(family, style)")]
    [InlineData("upper(\"family\")")]
    public void OldLanguageIsRejectedWithPosition(string expression)
    {
        var error = Assert.Single(Compile("  {" + expression + "} ").Errors);
        Assert.InRange(error.Offset, 3, expression.Length + 3);
    }

    [Theory]
    [InlineData("  {bad(family)}", "unknown-function", 3)]
    [InlineData("  {upper(bad)}", "unknown-field", 9)]
    [InlineData("  {upper(family,style)}", "invalid-format-argument", 15)]
    [InlineData("  {family", "unclosed-template-expression", 9)]
    [InlineData("  { {family}}", "nested-template-brace", 4)]
    [InlineData("abc}", "invalid-path-literal", 3)]
    public void ErrorsPointIntoUntrimmedInput(string template, string code, int offset)
    {
        var error = Assert.Single(Compile(template).Errors);
        Assert.Equal(code, error.Code);
        Assert.Equal(offset, error.Offset);
    }

    [Fact]
    public void CaseFormattingIsInvariant()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new("tr-TR");
            var file = FontNamingTestData.File("a.otf", FontNamingTestData.Face(names: [(1, "Title i"), (2, "BOLD")]));
            Assert.Equal("TITLE I-bold", Expand(" { upper( family ) }-{ lower(style) } ", file).Components.Single());
            Assert.Equal("Title i-BOLD", Expand("{family}{styleSuffix}", file).Components.Single());
            Assert.Equal("Title i", Expand("{family}{upper(versionSuffix)}", file).Components.Single());
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void OptionalDirectoryLayersAreOmitted()
    {
        var file = FontNamingTestData.File("a.otf", FontNamingTestData.Face(names: [(1, "Demo")]));
        var result = Expand("{manufacturer}/{family}/{lower(styleSuffix)}", file, FontTemplateKind.Folder);
        Assert.Null(result.SkipReason);
        Assert.Equal(["Demo"], result.Components);
        Assert.Contains(result.Evidence.Fields, field => field.Name == "manufacturer" && field.MissingReason == "missing-field");
        Assert.NotNull(Expand("{fullName}/{family}", file, FontTemplateKind.Folder).SkipReason);
    }

    [Fact]
    public void AllOptionalLayersAreNotAnEmptyFolderConfiguration()
    {
        var file = FontNamingTestData.File("a.otf", FontNamingTestData.Face(names: [(1, "Demo")]));
        var omitted = Expand("{manufacturer}/{style}", file, FontTemplateKind.Folder);
        Assert.Empty(omitted.Components);
        Assert.False(omitted.PreserveSourceDirectory);
        Assert.True(Expand("  ", file, FontTemplateKind.Folder).PreserveSourceDirectory);
        Assert.NotNull(Expand("{manufacturer}", file).SkipReason);
        Assert.NotNull(Expand("???{manufacturer}", file, FontTemplateKind.Folder).SkipReason);
    }

    [Fact]
    public void ExpansionDoesNotReparseMetadataBraces()
    {
        var file = FontNamingTestData.File("a.otf", FontNamingTestData.Face(names: [(1, "{style}"), (8, "Acme/Foundry")]));
        Assert.Equal(["Acme-Foundry", "{style}"], Expand("{manufacturer}/{family}", file, FontTemplateKind.Folder).Components);
        Assert.Equal("{style}", Expand("{family}", file).Components.Single());
    }

    [Fact]
    public void SuffixTailIsProtectedOnlyAtTheEnd()
    {
        var file = FontNamingTestData.File("a.ttc", FontNamingTestData.Face(names: [(1, "Demo"), (2, "Bold")]) with { Version = "1.0" });
        Assert.Equal("-Bold-Collection-v1.0", Expand(FontNamingRules.DefaultFileNameTemplate, file).ProtectedSuffix);
        Assert.Equal("-BOLD-collection-v1.0", Expand("{family}{upper(styleSuffix)}{lower(collectionSuffix)}{versionSuffix}", file).ProtectedSuffix);
        Assert.Equal("", Expand("{styleSuffix}{family}", file).ProtectedSuffix);
        Assert.Equal("-v1.0", Expand("{family}{styleSuffix}-x{versionSuffix}", file).ProtectedSuffix);
    }

    [Fact]
    public void EvidenceContainsOnlyReferencedFieldsAndDependenciesOnce()
    {
        var file = FontNamingTestData.File("a.otf", FontNamingTestData.Face(names: [(1, "Demo"), (2, "Bold")]));
        var result = Expand("{family}{family}{styleSuffix}", file);
        Assert.Equal(1, result.Evidence.Fields.Count(field => field.Name == "family"));
        Assert.DoesNotContain(result.Evidence.Fields, field => field.Name == "manufacturer");
        Assert.Contains(result.Evidence.Selections, item => item.Kind == "legacy-pair");
    }

    [Theory]
    [InlineData("A//B")]
    [InlineData("/A")]
    [InlineData("A/")]
    [InlineData("A/../{family}")]
    [InlineData("C:\\{family}")]
    [InlineData(".")]
    [InlineData("..")]
    public void UnsafeLiteralPathsAreRejected(string template)
    {
        Assert.NotEmpty(Compile(template, FontTemplateKind.Folder).Errors);
    }

    [Fact]
    public void ComponentNormalizationIsIdempotent()
    {
        var file = FontNamingTestData.File("a.otf", FontNamingTestData.Face(names: [(1, "CON")]));
        Assert.Equal("_CON", Expand("{family}", file).Components.Single());
        Assert.Equal("_CON", Expand("_CON", file).Components.Single());
        Assert.Equal("é", Expand("e\u0301", file).Components.Single());
        Assert.Equal("invalid-unicode", Expand("\uD800", file).SkipReason);
    }

    [Fact]
    public void TemplateBudgetsAreEnforced()
    {
        Assert.Empty(Compile(new string('a', 16384)).Errors);
        Assert.NotEmpty(Compile(new string('a', 16385)).Errors);
        Assert.Empty(Compile(string.Concat(Enumerable.Repeat("{family}", 512))).Errors);
        Assert.NotEmpty(Compile(string.Concat(Enumerable.Repeat("{family}", 513))).Errors);
        var file = FontNamingTestData.File("a.otf", FontNamingTestData.Face(names: [(1, new string('a', 32768))]));
        Assert.Equal("evaluation-limit-exceeded", Expand("{family}{family}{family}", file).SkipReason);
        string folders = string.Join('/', Enumerable.Repeat("{family}", 33));
        Assert.Equal("evaluation-limit-exceeded", Expand(folders, file, FontTemplateKind.Folder).SkipReason);
        Assert.Equal(65536, Expand("{family}{family}", file).Components.Single().Length);
    }

    [Fact]
    public void RulesOnlyAcceptCurrentVersion()
    {
        Assert.Empty(FontNamingTemplate.ValidateRules(new("{family}", "", false, 2)));
        Assert.Equal("unsupported-rule-version", FontNamingTemplate.ValidateRules(new("{family}", "", false, 1)).Single().Code);
        Assert.NotEmpty(FontNamingTemplate.ValidateRules(new("{name}", "", false, 2)));
    }

    private static FontNamingCompilation Compile(string template, FontTemplateKind kind = FontTemplateKind.FileName)
        => FontNamingTemplate.Compile(template, kind);

    private static FontTemplateResult Expand(string template, FontFileRecord file, FontTemplateKind kind = FontTemplateKind.FileName)
    {
        var compiled = Compile(template, kind);
        Assert.Empty(compiled.Errors);
        return FontNamingTemplate.Expand(Assert.IsType<FontNamingProgram>(compiled.Program), FontNamingFieldResolver.Resolve(file));
    }
}
