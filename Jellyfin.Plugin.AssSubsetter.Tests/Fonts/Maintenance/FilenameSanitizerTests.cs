using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Maintenance;

public class FilenameSanitizerTests
{
    [Theory]
    [InlineData("Example Sans", "Example Sans")]
    [InlineData("CON", "_CON")]
    [InlineData("aux.txt", "_aux.txt")]
    [InlineData("字體 名稱", "字體 名稱")]
    [InlineData("Bad<>:\"/\\|?*Name... ", "Bad-Name")]
    [InlineData(" a---b__c  ", "a-b-c")]
    public void Sanitize_ShouldProducePortableUnicodeComponent(string source, string expected)
    {
        Assert.Equal(expected, FilenameSanitizer.Sanitize(source));
    }

    [Fact]
    public void LimitPath_ShouldPreserveExtensionAndFingerprintSuffix()
    {
        string name = FilenameSanitizer.LimitPath(new string('a', 300), ".ttf", "-a1b2c3d4", 80);
        Assert.True(name.Length <= 80);
        Assert.EndsWith("-a1b2c3d4.ttf", name, StringComparison.Ordinal);
    }

    [Fact]
    public void LimitPath_ShouldRespectPortableUtf8ByteLimits()
    {
        string name = FilenameSanitizer.LimitPath(new string('字', 200), ".ttf", "-a1b2c3d4", 240);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(name) <= 240);
    }
}
