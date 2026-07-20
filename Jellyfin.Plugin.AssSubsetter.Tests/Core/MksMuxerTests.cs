using System.Diagnostics;
using Jellyfin.Plugin.AssSubsetter.Core;
using Jellyfin.Plugin.AssSubsetter.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Core;

public class MksMuxerTests : IDisposable
{
    private readonly string _tempPath = Path.Join(Path.GetTempPath(), "MksMuxerTests_" + Guid.NewGuid().ToString("N"));

    public MksMuxerTests()
    {
        Directory.CreateDirectory(_tempPath);
    }

    [Fact]
    public void BuildArguments_ShouldCopyAssAndDescribeEveryAttachment()
    {
        var fonts = new[]
        {
            new SubsetFontAttachment("A B.ttf", "font/ttf", [1]),
            new SubsetFontAttachment("C.otf", "font/otf", [2])
        };

        IReadOnlyList<string> args = MksMuxer.BuildArguments(
            "C:\\work dir\\sub.ass",
            ["C:\\work dir\\A B.ttf", "C:\\work dir\\C.otf"],
            fonts,
            "C:\\cache dir\\out.partial.mks");

        Assert.Contains("-c:s", args);
        Assert.Contains("copy", args);
        Assert.Equal(2, args.Count(value => value == "-attach"));
        Assert.Contains("-metadata:s:t:0", args);
        Assert.Contains("filename=A B.ttf", args);
        Assert.Contains("mimetype=font/otf", args);
        Assert.Contains("-metadata:s:t:1", args);
        Assert.Contains("-f", args);
        Assert.Contains("matroska", args);
        Assert.DoesNotContain(args, value => value.Contains('"'));
    }

    [Fact]
    public async Task MuxAsync_ShouldPromoteNonEmptyOutput_WhenRunnerSucceeds()
    {
        string output = Path.Join(_tempPath, "subtitle.mks");
        var artifact = CreateArtifact();
        var muxer = CreateMuxer((startInfo, _) =>
        {
            File.WriteAllText(startInfo.ArgumentList[^1], "mks-data");
            return Task.FromResult(0);
        });

        bool result = await muxer.MuxAsync(artifact, output, TestContext.Current.CancellationToken);

        Assert.True(result);
        Assert.Equal("mks-data", File.ReadAllText(output));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task MuxAsync_ShouldNotPromoteOutput_WhenRunnerFails()
    {
        string output = Path.Join(_tempPath, "subtitle.mks");
        var muxer = CreateMuxer((startInfo, _) =>
        {
            File.WriteAllText(startInfo.ArgumentList[^1], "partial-data");
            return Task.FromResult(1);
        });

        bool result = await muxer.MuxAsync(CreateArtifact(), output, TestContext.Current.CancellationToken);

        Assert.False(result);
        Assert.False(File.Exists(output));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task MuxAsync_ShouldCleanUp_WhenCancelled()
    {
        string output = Path.Join(_tempPath, "subtitle.mks");
        var muxer = CreateMuxer((_, token) => Task.FromCanceled<int>(new CancellationToken(true)));

        bool result = await muxer.MuxAsync(CreateArtifact(), output, new CancellationToken(true));

        Assert.False(result);
        Assert.False(File.Exists(output));
        AssertNoTemporaryFiles();
    }

    private static SubsetArtifact CreateArtifact()
    {
        return new SubsetArtifact(
            "[Script Info]\nScriptType: v4.00+",
            [new SubsetFontAttachment("TESTFONT.ttf", "font/ttf", [1, 2, 3])]);
    }

    private static MksMuxer CreateMuxer(Func<ProcessStartInfo, CancellationToken, Task<int>> runner)
    {
        return new MksMuxer(NullLogger<MksMuxer>.Instance, () => "ffmpeg", runner);
    }

    private void AssertNoTemporaryFiles()
    {
        Assert.Empty(Directory.EnumerateFiles(_tempPath, "*.partial.mks"));
        Assert.Empty(Directory.EnumerateDirectories(_tempPath, ".mks-*"));
    }

    public void Dispose()
    {
        Directory.Delete(_tempPath, true);
    }
}
