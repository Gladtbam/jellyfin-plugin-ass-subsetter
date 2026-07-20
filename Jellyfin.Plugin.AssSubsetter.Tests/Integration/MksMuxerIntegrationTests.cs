using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Jellyfin.Plugin.AssSubsetter.Core;
using Jellyfin.Plugin.AssSubsetter.Models;
using Jellyfin.Plugin.AssSubsetter.Native;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Integration;

public class MksMuxerIntegrationTests
{
    [Fact]
    [SuppressMessage("Security", "CA3006:Review code for process command injection vulnerabilities", Justification = "The opt-in test invokes ffprobe located beside the verified Jellyfin FFmpeg executable.")]
    public async Task MuxAsync_WithJellyfinFfmpeg_ShouldContainAssAndFontAttachment()
    {
        string? ffmpeg = FfmpegLocator.FindPath();
        string? fontPath = Environment.GetEnvironmentVariable("MKS_TEST_FONT");
        if (ffmpeg is null || Environment.GetEnvironmentVariable("RUN_FFMPEG_INTEGRATION") != "1" ||
            string.IsNullOrWhiteSpace(fontPath) || !File.Exists(fontPath))
        {
            Assert.Skip("Requires RUN_FFMPEG_INTEGRATION=1, Jellyfin FFmpeg, and an existing MKS_TEST_FONT.");
        }

        string directory = Path.Join(Path.GetTempPath(), "MksIntegration_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string output = Path.Join(directory, "subtitle.mks");
            byte[] font = await File.ReadAllBytesAsync(fontPath, TestContext.Current.CancellationToken);
            var artifact = new SubsetArtifact(
                "[Script Info]\nScriptType: v4.00+\n[V4+ Styles]\nFormat: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding\nStyle: Default,TESTFONT,20,&H00FFFFFF,&H000000FF,&H00000000,&H00000000,0,0,0,0,100,100,0,0,1,2,0,2,10,10,10,1\n[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text\nDialogue: 0,0:00:00.00,0:00:01.00,Default,,0,0,0,,Test",
                [new SubsetFontAttachment("TESTFONT.ttf", "font/ttf", font)]);
            var muxer = new MksMuxer(NullLogger<MksMuxer>.Instance, () => ffmpeg);

            Assert.True(await muxer.MuxAsync(artifact, output, TestContext.Current.CancellationToken));

            string ffprobe = Path.Join(Path.GetDirectoryName(ffmpeg)!, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            var startInfo = new ProcessStartInfo(ffprobe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            foreach (string argument in new[] { "-v", "error", "-show_streams", "-of", "json", output })
            {
                startInfo.ArgumentList.Add(argument);
            }

            using Process process = Process.Start(startInfo)!;
            string json = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);

            Assert.Equal(0, process.ExitCode);
            Assert.Contains("\"codec_name\": \"ass\"", json, StringComparison.Ordinal);
            Assert.Contains("\"codec_type\": \"attachment\"", json, StringComparison.Ordinal);
            Assert.Contains("\"filename\": \"TESTFONT.ttf\"", json, StringComparison.Ordinal);
            Assert.Contains("\"mimetype\": \"font/ttf\"", json, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
