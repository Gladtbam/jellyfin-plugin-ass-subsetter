using System.Text.Json;
using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Maintenance;

public sealed class FontRenamePlannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FontRenamePlan_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void FolderTemplateBuildsWithinMostSpecificRoot()
    {
        string nestedRoot = Path.Combine(_root, "Adobe");
        var file = Source(Path.Combine(nestedRoot, "old", "A.ttf"));
        var policy = Policy(_root + ";" + nestedRoot);
        var rules = new FontNamingRules("{family}-{style}", "{manufacturer}/{family}", false);
        var plan = Build([file], rules, policy);

        Assert.Equal(Path.Combine(nestedRoot, "Adobe", "Aparajita", "Aparajita-Bold.ttf"), plan.Items.Single().NewPath);
        Assert.Equal(nestedRoot, plan.Items.Single().SourceRoot);
        Assert.False(Directory.Exists(Path.Combine(nestedRoot, "Adobe")));
        Assert.Contains(plan.Items.Single().NamingEvidence.Fields, field => field.Name == "manufacturer");
    }

    [Theory]
    [InlineData(".TTF", ".ttf")]
    [InlineData(".OTF", ".otf")]
    [InlineData(".TTC", ".ttc")]
    [InlineData(".OTC", ".otc")]
    public void PreservesContainerAndSafePrefix(string original, string expected)
    {
        var file = Source(Path.Combine(_root, "old", "A" + original)) with
        {
            Faces = [FontNamingTestData.Face(names: [(1, "CON"), (2, "Regular")])]
        };
        var plan = Build([file], new("{family}-{style}", "", false));
        Assert.Equal(Path.Combine(_root, "old", "_CON-Regular" + expected), plan.Items.Single().NewPath);
    }

    [Fact]
    public void PreferredRootDoesNotAuthorizeMutation()
    {
        var file = Source(Path.Combine(_root, "font.ttf"));
        var policy = new FontDirectoryPolicyResolver(null, null, _root, false);
        Assert.Null(policy.GetOwningCustomRoot(file.Path));
        Assert.Equal("protected-or-not-custom", Build([file], new("{family}", "", false), policy).Items.Single().SkipReason);
    }

    [Fact]
    public void NestedMountIsRejected()
    {
        var file = Source(Path.Combine(_root, "old", "font.ttf"));
        Directory.CreateDirectory(Path.Combine(_root, "otherMount"));
        var directories = new PhysicalFontRenameDirectoryOperations(path => path.Contains("otherMount", StringComparison.Ordinal) ? "mount-b" : "mount-a");
        var plan = Build([file], new("{family}", "otherMount", false), directories: directories);
        Assert.Equal("different-filesystem", plan.Items.Single().SkipReason);
    }

    [Fact]
    public void CollisionAcrossSourceParentsIsStable()
    {
        var first = Source(Path.Combine(_root, "a", "old.ttf")) with { Id = 1 };
        var second = Source(Path.Combine(_root, "b", "old.ttf")) with
        {
            Id = 2,
            PhysicalIdentity = "second",
            Faces = [FontNamingTestData.Face(0, 1, (1, "Aparajita"), (2, "Bold"), (8, "Adobe"))]
        };
        var rules = new FontNamingRules("{family}-{style}", "organized", false);
        var plan = Build([first, second], rules);
        var reversed = Build([second, first], rules);
        Assert.Equal(plan.Items.OrderBy(item => item.File.Id).Select(item => item.NewPath), reversed.Items.OrderBy(item => item.File.Id).Select(item => item.NewPath));
        Assert.Equal(2, plan.Items.Select(item => item.NewPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(plan.Items, item => Assert.Null(item.SkipReason));
    }

    [Fact]
    public void SecondPreviewKeepsOwnTarget()
    {
        var file = Source(Path.Combine(_root, "organized", "Aparajita-Bold.ttf"));
        var competitor = Source(Path.Combine(_root, "old", "x.ttf")) with { Id = 2, PhysicalIdentity = "other" };
        var plan = Build([competitor, file], new("{family}-{style}", "organized", false));
        var own = plan.Items.Single(item => item.File.Id == 1);
        Assert.Equal(file.Path, own.NewPath);
        Assert.Equal("already-normalized", own.SkipReason);
        Assert.NotEqual(file.Path, plan.Items.Single(item => item.File.Id == 2).NewPath);
    }

    [Fact]
    public void TruncatedTargetsKeepTheirExistingOwnerAcrossNameGroups()
    {
        string stem = new('P', 240 - System.Text.Encoding.UTF8.GetByteCount(_root) - 1 - 4);
        var competitor = Source(Path.Combine(_root, "old.ttf")) with
        {
            Id = 2,
            Faces = [FontNamingTestData.Face(names: [(1, stem + "A")])]
        };
        var owner = Source(Path.Combine(_root, stem + ".ttf")) with
        {
            Faces = [FontNamingTestData.Face(names: [(1, stem + "Z")])]
        };
        var rules = new FontNamingRules("{family}", "", false);
        var plan = Build([competitor, owner], rules);
        var own = plan.Items.Single(item => item.File.Id == owner.Id);
        Assert.Equal(owner.Path, own.NewPath);
        Assert.Equal("already-normalized", own.SkipReason);
        var other = plan.Items.Single(item => item.File.Id == competitor.Id);
        Assert.Null(other.SkipReason);
        Assert.NotEqual(owner.Path, other.NewPath);
        Assert.Equal(plan.Items.Select(item => item.NewPath), Build([owner, competitor], rules).Items.Select(item => item.NewPath));
        Assert.Equal("font-fixture", File.ReadAllText(owner.Path));
    }

    [Fact]
    public void OccupiedExternalTargetIsSkippedWithoutOverwrite()
    {
        var file = Source(Path.Combine(_root, "old.ttf"));
        string target = Path.Combine(_root, "Aparajita-Bold.ttf");
        File.WriteAllText(target, "keep me");
        Assert.Equal("target-occupied", Build([file], new("{family}-{style}", "", false)).Items.Single().SkipReason);
        Assert.Equal("keep me", File.ReadAllText(target));
    }

    [Theory]
    [InlineData("/absolute")]
    [InlineData("C:/absolute")]
    [InlineData("a//b")]
    [InlineData("../b")]
    [InlineData("a/./b")]
    public void UnsafeLiteralPathsAreRejected(string folder)
    {
        var file = Source(Path.Combine(_root, "old.ttf"));
        Assert.Throws<ArgumentException>(() => Build([file], new("{family}", folder, false)));
    }

    [Fact]
    public void TargetDirectoryBudgetIsNotTruncated()
    {
        var file = Source(Path.Combine(_root, "old.ttf"));
        var plan = Build([file], new("{family}", new string('a', 240), false));
        Assert.Equal("path-too-long", plan.Items.Single().SkipReason);
        Assert.False(Directory.Exists(Path.Combine(_root, new string('a', 240))));
    }

    [Fact]
    public void ProtectedTargetIsRejected()
    {
        var file = Source(Path.Combine(_root, "old.ttf"));
        string target = Path.Combine(_root, "protected");
        Directory.CreateDirectory(target);
        var policy = new FontDirectoryPolicyResolver(_root, target, null, false);
        Assert.NotNull(Build([file], new("{family}", "protected", false), policy).Items.Single().SkipReason);
    }

    [Fact]
    public void PlanJsonRoundTripsRulesAndEvidence()
    {
        var file = Source(Path.Combine(_root, "old.ttf"));
        var rules = new FontNamingRules("{family}-{style}", "organized", true);
        var plan = Build([file], rules);
        var loaded = JsonSerializer.Deserialize<FontRenameSnapshot>(JsonSerializer.Serialize(plan))!;
        Assert.Equal(rules, loaded.Rules);
        Assert.Equal(rules.GetFingerprint(), loaded.RuleFingerprint);
        Assert.Equal(_root, loaded.Items.Single().SourceRoot);
        Assert.Equal("Aparajita", loaded.Items.Single().NamingEvidence.Fields.Single(field => field.Name == "family").Value);
    }

    [Fact]
    public void TruncationDoesNotProduceExtensionOnlyName()
    {
        int childLength = 240 - System.Text.Encoding.UTF8.GetByteCount(_root) - 1 - 1 - 4 - 2;
        string parent = Path.Combine(_root, new string('a', childLength));
        var file = Source(Path.Combine(parent, "x.ttf")) with { Faces = [FontNamingTestData.Face(names: [(1, "汉")])] };
        Assert.NotNull(Build([file], new("{family}", "", false)).Items.Single().SkipReason);
    }

    [Fact]
    public void TargetCannotChangeOwningCustomRoot()
    {
        var file = Source(Path.Combine(_root, "old.ttf"));
        string nested = Path.Combine(_root, "nested");
        Directory.CreateDirectory(nested);
        var plan = Build([file], new("{family}", "nested", false), Policy(_root + ";" + nested));
        Assert.Equal("target-root-changed", plan.Items.Single().SkipReason);
    }


    [Fact]
    public void DefaultVersionAppliesWithoutCollisionOrOwnBaseExemption()
    {
        var source = Source(Path.Combine(_root, "Aparajita-Bold.ttf"));
        var face = source.Faces[0] with { Version = "1.004" };
        var plan = Build([source with { Faces = [face] }], new(FontNamingRules.DefaultFileNameTemplate, "", false));
        Assert.Equal(Path.Combine(_root, "Aparajita-Bold-v1.004.ttf"), plan.Items.Single().NewPath);
        Assert.Null(plan.Items.Single().SkipReason);
        Assert.Null(plan.Items.Single().CollisionFingerprint);
        Assert.Contains(plan.Items.Single().NamingEvidence.Fields, item => item.Name == "version" && item.Value == "1.004");
    }

    [Fact]
    public void ExplicitTemplateWithoutVersionNeverGetsImplicitVersion()
    {
        var first = Source(Path.Combine(_root, "a.ttf")) with { Faces = [FontNamingTestData.Face(names: [(1, "Family")]) with { Version = "1.004" }] };
        var second = Source(Path.Combine(_root, "b.ttf")) with { Id = 2, Faces = [FontNamingTestData.Face(0, 1, (1, "Family")) with { Version = "2.0" }] };
        var plan = Build([second, first], new("{family}", "", false));
        Assert.All(plan.Items, item => Assert.DoesNotContain("-v", item.NewPath!, StringComparison.Ordinal));
        Assert.All(plan.Items, item => Assert.Contains(item.NamingEvidence.Fields, field => field.Name == "version"));
        Assert.Equal(1, plan.Items.Count(item => item.CollisionFingerprint is not null));
    }

    [Fact]
    public void MissingManufacturerLayerUsesRootInsteadOfOriginalParent()
    {
        var source = Source(Path.Combine(_root, "old", "a.ttf")) with { Faces = [FontNamingTestData.Face(names: [(1, "Family")])] };
        Assert.Equal(Path.Combine(_root, "Family", "Family.ttf"), Build([source], new("{family}", "{manufacturer}/{family}", false)).Items.Single().NewPath);
        Assert.Equal(_root, Path.GetDirectoryName(Build([source], new("{family}", "{manufacturer}/{style}", false)).Items.Single().NewPath));
        Assert.Equal(Path.GetDirectoryName(source.Path), Path.GetDirectoryName(Build([source], new("{family}", "", false)).Items.Single().NewPath));
    }

    [Fact]
    public void ProtectedTailSurvivesUnicodeTruncation()
    {
        var face = FontNamingTestData.Face(names: [(1, string.Concat(Enumerable.Repeat("😀汉", 100))), (2, "Bold")]) with { Version = "1.004" };
        var first = Source(Path.Combine(_root, "a.otf")) with { Faces = [face] };
        var second = Source(Path.Combine(_root, "b.otf")) with { Id = 2, Faces = [face with { StrictFingerprint = Enumerable.Repeat((byte)0xab, 32).ToArray() }] };
        var plan = Build([first, second], new(FontNamingRules.DefaultFileNameTemplate, "", false));
        Assert.All(plan.Items, item => Assert.Null(item.SkipReason));
        Assert.All(plan.Items, item => Assert.True(System.Text.Encoding.UTF8.GetByteCount(item.NewPath!) <= 240));
        Assert.EndsWith("-Bold-v1.004.otf", plan.Items[0].NewPath, StringComparison.Ordinal);
        Assert.EndsWith("-Bold-v1.004-abababab.otf", plan.Items[1].NewPath, StringComparison.Ordinal);
    }

    [Fact]
    public void SecondPreviewKeepsFingerprintOwnerWithIdenticalCompetitor()
    {
        var owner = Source(Path.Combine(_root, "Family.ttf")) with { Id = 1, Faces = [FontNamingTestData.Face(names: [(1, "Family")])] };
        var fingerprintOwner = Source(Path.Combine(_root, "Family-01010101.ttf")) with { Id = 2, Faces = [FontNamingTestData.Face(0, 1, (1, "Family"))] };
        var competitor = Source(Path.Combine(_root, "a.ttf")) with { Id = 3, Faces = fingerprintOwner.Faces };
        var rules = new FontNamingRules("{family}", "", false);
        foreach (var files in new[] { new[] { competitor, fingerprintOwner, owner }, new[] { owner, fingerprintOwner, competitor } })
        {
            var plan = Build(files, rules);
            Assert.Equal("already-normalized", plan.Items.Single(item => item.File.Id == 1).SkipReason);
            var retained = plan.Items.Single(item => item.File.Id == 2);
            Assert.Equal(fingerprintOwner.Path, retained.NewPath);
            Assert.Equal("already-normalized", retained.SkipReason);
            Assert.Equal("fingerprint-collision", plan.Items.Single(item => item.File.Id == 3).SkipReason);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExistingFingerprintTargetsWinAcrossNameGroups(bool chained)
    {
        var owner = Source(Path.Combine(_root, "Family.ttf")) with { Id = 1, Faces = [FontNamingTestData.Face(names: [(1, "Family")])] };
        var fingerprintOwner = Source(Path.Combine(_root, "Family-01010101.ttf")) with { Id = 2, Faces = [FontNamingTestData.Face(0, 1, (1, "Family"))] };
        string incomingFamily = chained ? "Family-01010101-02020202" : "Family-01010101";
        var incoming = Source(Path.Combine(_root, "incoming.ttf")) with { Id = 4, Faces = [FontNamingTestData.Face(0, 3, (1, incomingFamily))] };
        var files = new List<FontFileRecord> { owner, fingerprintOwner, incoming };
        if (chained)
        {
            files.Add(Source(Path.Combine(_root, "Family-01010101-02020202.ttf")) with { Id = 3, Faces = [FontNamingTestData.Face(0, 2, (1, "Family-01010101"))] });
        }

        foreach (var order in new[] { files.ToArray(), files.AsEnumerable().Reverse().ToArray() })
        {
            var plan = Build(order, new("{family}", "", false));
            Assert.All(plan.Items.Where(item => item.File.Id != 4), item =>
            {
                Assert.Equal(item.File.Path, item.NewPath);
                Assert.Equal("already-normalized", item.SkipReason);
            });
            var proposed = plan.Items.Single(item => item.File.Id == 4);
            Assert.Null(proposed.SkipReason);
            Assert.Equal(Path.Combine(_root, incomingFamily + "-03030303.ttf"), proposed.NewPath);
            Assert.Equal("03030303", proposed.CollisionFingerprint);
        }
    }

    [Fact]
    public void SuccessfulDefaultPlanIsStableOnSecondPreview()
    {
        var first = Source(Path.Combine(_root, "a.ttf"));
        var second = Source(Path.Combine(_root, "b.ttf")) with { Id = 2, Faces = [first.Faces[0] with { StrictFingerprint = Enumerable.Repeat((byte)1, 32).ToArray() }] };
        var rules = new FontNamingRules(FontNamingRules.DefaultFileNameTemplate, "", false);
        var initial = Build([second, first], rules);
        foreach (var item in initial.Items) { File.Move(item.File.Path, item.NewPath!); }
        var moved = initial.Items.Select(item => item.File with { Path = item.NewPath! }).Reverse().ToArray();
        var next = Build(moved, rules);
        Assert.All(next.Items, item => Assert.Equal("already-normalized", item.SkipReason));
        Assert.Equal(initial.Items.OrderBy(item => item.File.Id).Select(item => item.NewPath), next.Items.OrderBy(item => item.File.Id).Select(item => item.NewPath));
    }

    [Fact]
    public void UnreferencedBadFingerprintDoesNotBlockUniqueName()
    {
        var source = Source(Path.Combine(_root, "old.ttf")) with { Faces = [FontNamingTestData.Face(names: [(1, "Family")]) with { StrictFingerprint = new byte[4] }] };
        Assert.Null(Build([source], new("{family}", "", false)).Items.Single().SkipReason);
        Assert.Contains("invalid-fingerprint", Build([source], new("{fingerprint}", "", false)).Items.Single().SkipReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void CaseOnlyTargetIsNotAlreadyNormalized()
    {
        var source = Source(Path.Combine(_root, "APARAJITA-BOLD.TTF"));
        var result = Build([source], new(FontNamingRules.DefaultFileNameTemplate, "", false)).Items.Single();
        Assert.Null(result.SkipReason);
        Assert.Equal(Path.Combine(_root, "Aparajita-Bold.ttf"), result.NewPath);
    }

    [Fact]
    public void TruncationCannotCreateUnsafeDeviceName()
    {
        int remaining = 240 - System.Text.Encoding.UTF8.GetByteCount(_root) - 1 - 1 - 4 - 3;
        var source = Source(Path.Combine(_root, new string('a', remaining), "x.ttf")) with { Faces = [FontNamingTestData.Face(names: [(1, "CONmore")])] };
        var result = Build([source], new("{family}", "", false)).Items.Single();
        Assert.True(result.NewPath is null || Path.GetFileNameWithoutExtension(result.NewPath) != "CON");
        if (result.NewPath is not null) { Assert.True(System.Text.Encoding.UTF8.GetByteCount(result.NewPath) <= 240); }
    }
    [Fact]
    public void ShortFingerprintCollisionIsSkipped()
    {
        var first = Source(Path.Combine(_root, "a.ttf")) with { Id = 1 };
        var second = Source(Path.Combine(_root, "b.ttf")) with { Id = 2, Faces = [first.Faces[0] with { StrictFingerprint = Convert.FromHexString("aabbccdd" + new string('1', 56)) }] };
        var third = Source(Path.Combine(_root, "c.ttf")) with { Id = 3, Faces = [first.Faces[0] with { StrictFingerprint = Convert.FromHexString("aabbccdd" + new string('2', 56)) }] };
        var rules = new FontNamingRules("{family}", "", false);
        var plan = Build([third, first, second], rules);
        Assert.Equal("fingerprint-collision", plan.Items.Single(item => item.File.Id == 3).SkipReason);
        Assert.EndsWith("-aabbccdd.ttf", plan.Items.Single(item => item.File.Id == 2).NewPath, StringComparison.Ordinal);
        Assert.Equal(plan.Items.Select(item => item.NewPath), Build([second, third, first], rules).Items.Select(item => item.NewPath));
    }
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private FontFileRecord Source(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "font-fixture");
        return FontNamingTestData.File(path, FontNamingTestData.Face(names: [(1, "Aparajita"), (2, "Bold"), (8, "Adobe")]));
    }

    private FontDirectoryPolicyResolver Policy(string? custom = null) => new(custom ?? _root, null, null, false);

    private FontRenameSnapshot Build(IReadOnlyList<FontFileRecord> files, FontNamingRules rules, FontDirectoryPolicyResolver? policy = null, IFontRenameDirectoryOperations? directories = null)
        => new FontRenamePlanner(directories ?? new PhysicalFontRenameDirectoryOperations()).Build(1, 2, files, new HashSet<long>(), rules, policy ?? Policy());
}
