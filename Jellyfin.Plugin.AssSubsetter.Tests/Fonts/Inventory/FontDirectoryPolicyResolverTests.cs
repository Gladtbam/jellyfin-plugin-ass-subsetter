using Jellyfin.Plugin.AssSubsetter.Fonts.Domain;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Inventory;

public sealed class FontDirectoryPolicyResolverTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "FontPolicyTests_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void GetPolicy_ShouldCombineResolvedCustomProtectedAndPreferredAncestors()
    {
        string custom = Path.Join(_root, "custom");
        string preferred = Path.Join(custom, "preferred");
        Directory.CreateDirectory(preferred);
        var resolver = new FontDirectoryPolicyResolver(custom, _root, preferred, includePlatformDefaults: false);

        FontDirectoryPolicy policy = resolver.GetPolicy(Path.Join(preferred, "example.otc"));

        Assert.True(policy.HasFlag(FontDirectoryPolicy.Custom));
        Assert.True(policy.HasFlag(FontDirectoryPolicy.Protected));
        Assert.True(policy.HasFlag(FontDirectoryPolicy.Preferred));
        Assert.True(FontDirectoryPolicyResolver.IsSupportedFont("example.otc"));
    }

    [Fact]
    public void GetPolicy_ShouldProtectWindowsPerUserFontDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string userFonts = Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "Windows",
            "Fonts");
        var resolver = new FontDirectoryPolicyResolver(userFonts, null, null);

        FontDirectoryPolicy policy = resolver.GetPolicy(Path.Join(userFonts, "user-font.ttf"));

        Assert.True(policy.HasFlag(FontDirectoryPolicy.Custom));
        Assert.True(policy.HasFlag(FontDirectoryPolicy.Protected));
    }

    [Fact]
    public void GetPolicy_ShouldKeepWindowsSystemFontDirectoryProtectedWhenItIsAlsoCustom()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string systemFonts = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");
        var resolver = new FontDirectoryPolicyResolver(systemFonts, null, null);

        FontDirectoryPolicy policy = resolver.GetPolicy(Path.Join(systemFonts, "system-font.ttf"));

        Assert.True(policy.HasFlag(FontDirectoryPolicy.Custom));
        Assert.True(policy.HasFlag(FontDirectoryPolicy.Protected));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
