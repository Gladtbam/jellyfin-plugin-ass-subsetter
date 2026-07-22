using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Core;

public sealed class AssToSupConverterTests : IDisposable
{
    private readonly string _tempDir = Path.Join(Path.GetTempPath(), $"ass-subsetter-sup-{Guid.NewGuid():N}");

    public AssToSupConverterTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public async Task ConvertAsync_ShouldPreserveExistingOutput_WhenConversionFails()
    {
        string input = Path.Join(_tempDir, "empty.ass");
        string output = Path.Join(_tempDir, "subtitle.sup");
        await File.WriteAllTextAsync(
            input,
            "[Events]\nFormat: Layer, Start, End, Style, Text",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(output, "old-cache", TestContext.Current.CancellationToken);
        using var converter = new AssToSupConverter(
            () => new PluginConfiguration(),
            NullLogger<AssToSupConverter>.Instance);

        bool result = await converter.ConvertAsync(
            input,
            output,
            1920,
            1080,
            TestContext.Current.CancellationToken);

        Assert.False(result);
        Assert.Equal("old-cache", await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_tempDir, "*.partial.sup"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, true);
        }
    }
}
