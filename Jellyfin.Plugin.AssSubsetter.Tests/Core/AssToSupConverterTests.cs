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
    public async Task ParseEvents_PreservesAnimationAfterTextCommas()
    {
        string input = Path.Join(_tempDir, "animation.ass");
        await File.WriteAllTextAsync(input,
            "[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:00.00,0:00:02.00,Default,,0,0,0,,Hello, {\\t(0,1000,\\fs60)}world",
            TestContext.Current.CancellationToken);
        var parse = typeof(AssToSupConverter).GetMethod("ParseAssEvents", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var events = (System.Collections.IEnumerable)parse.Invoke(null, [input])!;
        object ev = Assert.Single(events.Cast<object>());
        Assert.True((bool)ev.GetType().GetProperty("HasAnimation")!.GetValue(ev)!);
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
