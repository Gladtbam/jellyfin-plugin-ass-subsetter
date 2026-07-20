using Jellyfin.Plugin.AssSubsetter.Native;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Native;

public class FfmpegLocatorTests
{
    [Fact]
    public void FindPath_ShouldPreferEnvironmentOption()
    {
        string? result = FfmpegLocator.FindPath(
            "--ffmpeg=\"C:\\Jellyfin FFmpeg\\ffmpeg.exe\" --other=value",
            ["server", "--ffmpeg=C:\\command\\ffmpeg.exe"],
            path => path is "C:\\Jellyfin FFmpeg\\ffmpeg.exe" or "C:\\command\\ffmpeg.exe");

        Assert.Equal("C:\\Jellyfin FFmpeg\\ffmpeg.exe", result);
    }

    [Theory]
    [InlineData("--ffmpeg=/opt/jellyfin/ffmpeg", "/opt/jellyfin/ffmpeg")]
    [InlineData("--ffmpeg=\"C:\\Program Files\\Jellyfin\\ffmpeg.exe\"", "C:\\Program Files\\Jellyfin\\ffmpeg.exe")]
    public void FindPath_ShouldParseCommandLineArgument(string argument, string expected)
    {
        string? result = FfmpegLocator.FindPath(null, ["server", argument], path => path == expected);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void FindPath_ShouldPreserveSpacesInSingleCommandLineArgument()
    {
        const string expected = "C:\\Program Files\\Jellyfin\\ffmpeg.exe";

        string? result = FfmpegLocator.FindPath(null, ["server", $"--ffmpeg={expected}"], path => path == expected);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void FindPath_ShouldUseWellKnownPathAsLastFallback()
    {
        string? result = FfmpegLocator.FindPath(null, ["server"], path => path == "/usr/lib/jellyfin-ffmpeg/ffmpeg");

        Assert.Equal("/usr/lib/jellyfin-ffmpeg/ffmpeg", result);
    }

    [Fact]
    public void FindPath_ShouldReturnNull_WhenNoCandidateExists()
    {
        string? result = FfmpegLocator.FindPath("--ffmpeg=/missing/ffmpeg", ["server"], _ => false);

        Assert.Null(result);
    }
}
