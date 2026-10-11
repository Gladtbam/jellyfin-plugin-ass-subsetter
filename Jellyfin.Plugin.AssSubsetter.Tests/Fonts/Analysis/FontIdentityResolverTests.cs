using Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Analysis;

public sealed class FontIdentityResolverTests
{
    [Fact]
    public void Resolve_ShouldNormalizeNamesWithoutRemovingHyphensOrStyleWords()
    {
        InspectedFontFace face = Face(
            Name(6, "  Foo\u00a0-Bar   Italic  "),
            Name(16, "ＦＯＯ  Family"),
            Name(17, "Semi-Bold"));

        FontIdentityEvidence result = FontIdentityResolver.Resolve(face);

        Assert.Equal("FOO -BAR ITALIC", result.PostScriptName);
        Assert.Equal("FOO FAMILY", result.FamilyName);
        Assert.Equal("SEMI-BOLD", result.SubfamilyName);
    }

    [Theory]
    [InlineData(16, 17, "TYPOGRAPHIC FAMILY", "TYPOGRAPHIC STYLE")]
    [InlineData(21, 22, "WWS FAMILY", "WWS STYLE")]
    [InlineData(1, 2, "LEGACY FAMILY", "LEGACY STYLE")]
    public void Resolve_ShouldChooseFirstCompleteFamilyPair(
        ushort expectedFamilyId,
        ushort expectedStyleId,
        string expectedFamily,
        string expectedStyle)
    {
        var records = new List<OpenTypeNameRecord>
        {
            Name(1, "Legacy Family"),
            Name(2, "Legacy Style")
        };
        if (expectedFamilyId is 16 or 21)
        {
            records.Add(Name(21, "WWS Family"));
            records.Add(Name(22, "WWS Style"));
        }

        if (expectedFamilyId == 16)
        {
            records.Add(Name(16, "Typographic Family"));
            records.Add(Name(17, "Typographic Style"));
        }

        FontIdentityEvidence result = FontIdentityResolver.Resolve(Face(records.ToArray()));

        Assert.Equal(expectedFamily, result.FamilyName);
        Assert.Equal(expectedStyle, result.SubfamilyName);
        Assert.Equal(expectedFamilyId, result.FamilyNameId);
        Assert.Equal(expectedStyleId, result.SubfamilyNameId);
    }

    [Fact]
    public void Resolve_ShouldUseFullNameThenUniqueIdOnlyAsFallbackEvidence()
    {
        FontIdentityEvidence result = FontIdentityResolver.Resolve(Face(
            Name(4, "Example Full Regular"),
            Name(3, "Vendor:Example:2.0")));

        Assert.Equal("EXAMPLE FULL REGULAR", result.FullName);
        Assert.Equal("VENDOR:EXAMPLE:2.0", result.UniqueId);
        Assert.Equal("EXAMPLE FULL REGULAR", result.PrimaryIdentity);
    }

    [Fact]
    public void Resolve_ShouldExcludeVersionAndDescriptiveMetadataFromIdentityNames()
    {
        FontIdentityEvidence result = FontIdentityResolver.Resolve(Face(
            Name(0, "Shared Copyright"),
            Name(1, "Example Family"),
            Name(2, "Regular"),
            Name(5, "Version 1.0"),
            Name(13, "Shared License")));

        Assert.Contains("EXAMPLE FAMILY", result.AllNames);
        Assert.DoesNotContain("SHARED COPYRIGHT", result.AllNames);
        Assert.DoesNotContain("VERSION 1.0", result.AllNames);
        Assert.DoesNotContain("SHARED LICENSE", result.AllNames);
    }

    [Fact]
    public void Resolve_ShouldMakeVersionOrderableWhenHeadAndNameVersionAgree()
    {
        InspectedFontFace face = Face(Name(5, "Version 2.010"), Name(3, "Vendor:Example:2.010")) with
        {
            HeadRevision = "2.01",
            VendorId = "TEST"
        };

        FontVersionEvidence version = FontIdentityResolver.Resolve(face).Version;

        Assert.True(version.IsReliable);
        Assert.Equal(2.01m, version.Value);
        Assert.Contains("head.fontRevision", version.Sources);
        Assert.Contains("name.5", version.Sources);
        Assert.Contains("name.3", version.Sources);
        Assert.Contains("vendor", version.Sources);
    }

    [Fact]
    public void Resolve_ShouldRejectContradictoryVersionEvidence()
    {
        InspectedFontFace face = Face(Name(5, "Version 3.0"), Name(3, "Vendor:Example:2.0")) with
        {
            HeadRevision = "2.0",
            VendorId = "TEST"
        };

        FontVersionEvidence version = FontIdentityResolver.Resolve(face).Version;

        Assert.False(version.IsReliable);
        Assert.Null(version.Value);
    }

    private static InspectedFontFace Face(params OpenTypeNameRecord[] names)
    {
        return new InspectedFontFace
        {
            FaceIndex = 0,
            IsClassifiable = true,
            FamilyName = "Fallback Family",
            StyleName = "Regular",
            Technology = "TrueType",
            NameRecords = names
        };
    }

    private static OpenTypeNameRecord Name(ushort nameId, string value)
        => new(3, 1, 0x0409, nameId, value);
}
