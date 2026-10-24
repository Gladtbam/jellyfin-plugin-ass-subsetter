using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.Core;
using Jellyfin.Plugin.AssSubsetter.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Core;

public sealed class UnifiedSupFontTests
{
    private const string Ass = "[Script Info]\nScriptType: v4.00+\nPlayResX: 640\nPlayResY: 360\n[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\nStyle: Default,DejaVu Sans,32,&H00FFFFFF,&H00FFFFFF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,1,0,2,10,10,10,1\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,Hello";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompleteArtifactPreservesOutput(bool missing)
    {
        string output = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(output, "existing", TestContext.Current.CancellationToken);
            var artifact = missing ? new SubsetArtifact(Ass, []) { HasMissingFonts = true } : null;
            using var converter = new AssToSupConverter(() => new PluginConfiguration(), NullLogger<AssToSupConverter>.Instance, new PreparedProcessor(artifact));
            Assert.False(await converter.ConvertAsync("not-read.ass", output, 640, 360, TestContext.Current.CancellationToken));
            Assert.Equal("existing", await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken));
        }
        finally { File.Delete(output); }
    }

    [Fact]
    public async Task ProcessorValidatesMissingAndReadFontsBeforeSubsetting()
    {
        string input = Path.GetTempFileName();
        string font = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(input, Ass, TestContext.Current.CancellationToken);
            byte[] bytes = [1, 2, 3];
            await File.WriteAllBytesAsync(font, bytes, TestContext.Current.CancellationToken);
            foreach (bool missing in new[] { true, false })
            {
                bool observed = false;
                bool released = false;
                var lease = missing ? null : new Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance.FontFileLease(font, 0, () => { released = true; return ValueTask.CompletedTask; });
                var processor = new AssProcessor(() => new PluginConfiguration(), (_, _, _) => Task.FromResult(lease),
                    () => "test", new AssDocumentParser(), NullLogger<AssProcessor>.Instance,
                    (descriptor, characters, actualLease, actualBytes) =>
                    {
                        observed = true;
                        Assert.Equal("DejaVu Sans", descriptor.FontName);
                        Assert.Contains((uint)'H', characters);
                        Assert.Same(lease, actualLease);
                        Assert.False(released);
                        Assert.Equal(missing ? null : bytes, actualBytes);
                        throw new IOException("Dependency mismatch");
                    });
                Assert.Null(await processor.GenerateSubsetArtifactAsync(input, TestContext.Current.CancellationToken));
                Assert.True(observed);
                Assert.Equal(!missing, released);
            }
        }
        finally
        {
            File.Delete(input);
            File.Delete(font);
        }
    }

    [Fact]
    public async Task NativeLinuxMksPreservesFontPayloads()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("ASS_SUBSETTER_NATIVE_SUP_TEST") != "1")
        {
            Assert.Skip("Opt in with ASS_SUBSETTER_NATIVE_SUP_TEST=1 on Linux with ffmpeg and mkvextract installed.");
        }

        string directory = Path.Join(Path.GetTempPath(), "native-mks-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            byte[] font = await File.ReadAllBytesAsync(Environment.GetEnvironmentVariable("ASS_SUBSETTER_TEST_FONT")!, TestContext.Current.CancellationToken);
            var artifact = new SubsetArtifact(Ass,
                [new SubsetFontAttachment("same.ttf", "font/ttf", font), new SubsetFontAttachment("same.ttf", "font/ttf", [1, 2, 3, 4])]);
            string output = Path.Join(directory, "subtitle.mks");
            var muxer = new MksMuxer(NullLogger<MksMuxer>.Instance, () => "/usr/lib/jellyfin-ffmpeg/ffmpeg");
            Assert.True(await muxer.MuxAsync(artifact, output, TestContext.Current.CancellationToken));
            var start = new System.Diagnostics.ProcessStartInfo("mkvextract") { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { output, "attachments", "1:" + Path.Join(directory, "one.ttf"), "2:" + Path.Join(directory, "two.ttf") })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = System.Diagnostics.Process.Start(start)!;
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            Task<string> stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(process.ExitCode == 0, await stdout + await stderr);
            Assert.Equal(font, await File.ReadAllBytesAsync(Path.Join(directory, "one.ttf"), TestContext.Current.CancellationToken));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await File.ReadAllBytesAsync(Path.Join(directory, "two.ttf"), TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetDirectories(directory, ".mks-*"));
            Assert.Empty(Directory.GetFiles(directory, "*.partial.mks"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task NativeLinuxRendersOnlySuppliedFont()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("ASS_SUBSETTER_NATIVE_SUP_TEST") != "1")
        {
            Assert.Skip("Opt in on Linux with ASS_SUBSETTER_NATIVE_SUP_TEST=1 and ASS_SUBSETTER_TEST_FONT pointing to DejaVuSans.ttf.");
        }

        byte[] data = await File.ReadAllBytesAsync(Environment.GetEnvironmentVariable("ASS_SUBSETTER_TEST_FONT")!, TestContext.Current.CancellationToken);
        string output = Path.Join(Path.GetTempPath(), Guid.NewGuid() + ".sup");
        try
        {
            using var converter = new AssToSupConverter(() => new PluginConfiguration(), NullLogger<AssToSupConverter>.Instance,
                new PreparedProcessor(new SubsetArtifact(Ass, [new SubsetFontAttachment("font.ttf", "font/ttf", data)])));
            Assert.True(await converter.ConvertAsync("does-not-exist.ass", output, 640, 360, TestContext.Current.CancellationToken));
            Assert.True(new FileInfo(output).Length > 0);
            File.Delete(output);
            using var empty = new AssToSupConverter(() => new PluginConfiguration(), NullLogger<AssToSupConverter>.Instance,
                new PreparedProcessor(new SubsetArtifact(Ass, [])));
            Assert.False(await empty.ConvertAsync("does-not-exist.ass", output, 640, 360, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(output));

            string input = output + ".ass";
            try
            {
                await File.WriteAllTextAsync(input, Ass, TestContext.Current.CancellationToken);
                bool validated = false;
                bool released = false;
                var processor = new AssProcessor(() => new PluginConfiguration(),
                    (_, _, _) => Task.FromResult<Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance.FontFileLease?>(
                        new(Environment.GetEnvironmentVariable("ASS_SUBSETTER_TEST_FONT")!, 0, () => { released = true; return ValueTask.CompletedTask; })),
                    () => "SUPINTEGRATION", new AssDocumentParser(), NullLogger<AssProcessor>.Instance,
                    (_, _, lease, bytes) =>
                    {
                        Assert.NotNull(lease);
                        Assert.False(released);
                        Assert.Equal(data, bytes);
                        validated = true;
                    });
                using var shared = new AssToSupConverter(() => new PluginConfiguration(), NullLogger<AssToSupConverter>.Instance, processor);
                Assert.True(await shared.ConvertAsync(input, output, 640, 360, TestContext.Current.CancellationToken));
                Assert.True(validated);
                Assert.True(released);
                Assert.True(new FileInfo(output).Length > 0);
            }
            finally
            {
                File.Delete(input);
            }
        }
        finally { File.Delete(output); }
    }

    private sealed class PreparedProcessor(SubsetArtifact? artifact) : AssProcessor(
        () => new PluginConfiguration(), (_, _, _) => Task.FromResult<Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance.FontFileLease?>(null),
        () => "test", new AssDocumentParser(), NullLogger<AssProcessor>.Instance)
    {
        public override Task<SubsetArtifact?> GenerateSubsetArtifactAsync(string inputAssPath, CancellationToken cancellationToken = default)
            => Task.FromResult(artifact);
    }
}
