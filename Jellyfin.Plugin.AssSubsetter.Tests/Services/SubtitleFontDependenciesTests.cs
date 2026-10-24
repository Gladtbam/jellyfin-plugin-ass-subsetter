using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.AssSubsetter.Models;
using Jellyfin.Plugin.AssSubsetter.Services;
using Xunit;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Services;

public sealed class SubtitleFontDependenciesTests
{
    [Fact]
    public void IdentityDependsOnSelectedContentAndFace()
    {
        string first = SubtitleFontDependencies.FaceIdentity("abcd", 0);
        Assert.Equal(first, SubtitleFontDependencies.FaceIdentity("ABCD", 0));
        Assert.NotEqual(first, SubtitleFontDependencies.FaceIdentity("ffff", 0));
        Assert.NotEqual(first, SubtitleFontDependencies.FaceIdentity("abcd", 1));
    }

    [Fact]
    public void SelectionOrderDoesNotChangeFingerprint()
    {
        var first = new SubtitleFontDependencies(new Dictionary<string, string> { ["a"] = "hash:0", ["b"] = "missing" });
        var reordered = new SubtitleFontDependencies(new Dictionary<string, string> { ["b"] = "missing", ["a"] = "hash:0" });
        var resolved = new SubtitleFontDependencies(new Dictionary<string, string> { ["a"] = "hash:0", ["b"] = "other:0" });
        Assert.Equal(first.Fingerprint, reordered.Fingerprint);
        Assert.NotEqual(first.Fingerprint, resolved.Fingerprint);
    }

    [Fact]
    public void ValidationRequiresEveryRequestAndRejectsChangedSelection()
    {
        var descriptor = new FontDescriptor("Example", null, false, false);
        uint[] characters = [65];
        string key = SubtitleFontDependencies.RequestKey(descriptor, characters);
        using var missing = new SubtitleFontDependencyValidation(new SubtitleFontDependencies(new Dictionary<string, string> { [key] = "missing" }), () => { });
        Assert.False(missing.IsComplete);
        missing.Observe(descriptor, characters, null, null);
        Assert.True(missing.IsComplete);
        using var selected = new SubtitleFontDependencyValidation(new SubtitleFontDependencies(new Dictionary<string, string> { [key] = "hash:0" }), () => { });
        Assert.Throws<IOException>(() => selected.Observe(descriptor, characters, null, null));
        Assert.False(selected.IsComplete);
    }
}
