using System.Reflection;
using Jellyfin.Plugin.AssSubsetter.Configuration;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Configuration;

public class ConfigPageTests
{
    [Fact]
    public void EmbeddedConfigPage_ShouldContainGenerateMksOptionAndLocalizedLabels()
    {
        var html = GetConfigPageHtml();

        Assert.Contains("<option value=\"2\" data-i18n=\"optGenerateMks\">Generate MKS (Experimental)</option>", html, StringComparison.Ordinal);
        Assert.Contains("生成 MKS（实验性）", html, StringComparison.Ordinal);
        Assert.Contains("Generate MKS (Experimental)", html, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedConfigPage_ShouldNormalizeGenerateMksSerializedValues()
    {
        var html = GetConfigPageHtml();

        Assert.Contains("modeVal === 'GenerateMks' || modeVal === 2", html, StringComparison.Ordinal);
        Assert.Contains("modeVal = '2';", html, StringComparison.Ordinal);
    }

    private static string GetConfigPageHtml()
    {
        var assembly = typeof(PluginConfiguration).Assembly;
        using var stream = assembly.GetManifestResourceStream("Jellyfin.Plugin.AssSubsetter.Configuration.configPage.html");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }
}
