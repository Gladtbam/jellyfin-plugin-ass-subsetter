using System.Reflection;
using System.Runtime.InteropServices;
using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.Core;
using Jellyfin.Plugin.AssSubsetter.Native;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Core;

public sealed class AssToSupConverterTests : IDisposable
{
    private static readonly ComposeFrame _compose = typeof(AssToSupConverter)
        .GetMethod("ComposeFrame", BindingFlags.Static | BindingFlags.NonPublic)!.CreateDelegate<ComposeFrame>();
    private readonly string _tempDir = Path.Join(Path.GetTempPath(), $"ass-subsetter-sup-{Guid.NewGuid():N}");

    public AssToSupConverterTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void ComposeFrame_ClipsAndBlendsInTheSubtitleRegion()
    {
        using var images = new AssImages();
        IntPtr top = images.Add(1, 3, 2, 3, 2, 0x0000FF7F, [255, 0, 255, 255, 255, 255]);
        IntPtr first = images.Add(-1, 2, 4, 2, 5, 0xFF000000, [255, 255, 0, 255, 199, 255, 255, 255, 255, 199], top);

        byte[] rgba = _compose(first, 6, 5, out int x, out int y, out int width, out int height);

        Assert.Equal((0, 2, 3, 3), (x, y, width, height));
        Assert.Equal<byte>(
            [
                255, 0, 0, 255, 0, 0, 0, 0, 255, 0, 0, 255,
                255, 0, 0, 255, 127, 0, 128, 255, 255, 0, 0, 255,
                0, 0, 0, 0, 0, 0, 255, 128, 0, 0, 255, 128
            ], rgba);
    }

    [Fact]
    public void ComposeFrame_PreservesTransparentLayerBounds()
    {
        using var images = new AssImages();
        IntPtr image = images.Add(2, 3, 1, 1, 1, 0xFFFFFFFF, [255]);

        byte[] rgba = _compose(image, 6, 5, out int x, out int y, out int width, out int height);

        Assert.Equal((2, 3, 1, 1), (x, y, width, height));
        Assert.Equal<byte>([0, 0, 0, 0], rgba);
    }

    [Theory]
    [InlineData(6, 0)]
    [InlineData(-2, 0)]
    [InlineData(0, 5)]
    [InlineData(0, -2)]
    public void ComposeFrame_FullyClippedLayerReturnsEmptyRegion(int imageX, int imageY)
    {
        using var images = new AssImages();
        IntPtr image = images.Add(imageX, imageY, 2, 2, 2, 0xFFFFFFFF, [255, 255, 255, 255]);

        byte[] rgba = _compose(image, 6, 5, out int x, out int y, out int width, out int height);

        Assert.Equal((0, 0, 0, 0), (x, y, width, height));
        Assert.Empty(rgba);
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

    private delegate byte[] ComposeFrame(IntPtr firstImage, int width, int height, out int x, out int y, out int cropWidth, out int cropHeight);

    private sealed class AssImages : IDisposable
    {
        private readonly List<IntPtr> _allocations = [];

        internal IntPtr Add(int x, int y, int width, int height, int stride, uint color, byte[] coverage, IntPtr next = default)
        {
            IntPtr bitmap = Marshal.AllocHGlobal(coverage.Length);
            _allocations.Add(bitmap);
            Marshal.Copy(coverage, 0, bitmap, coverage.Length);
            IntPtr address = Marshal.AllocHGlobal(Marshal.SizeOf<LibassNative.AssImage>());
            _allocations.Add(address);
            Marshal.StructureToPtr(new LibassNative.AssImage
            {
                _w = width,
                _h = height,
                _stride = stride,
                _bitmap = bitmap,
                _color = color,
                _dstX = x,
                _dstY = y,
                _next = next
            }, address, false);
            return address;
        }

        public void Dispose()
        {
            foreach (IntPtr address in _allocations)
            {
                Marshal.FreeHGlobal(address);
            }
        }
    }
}
