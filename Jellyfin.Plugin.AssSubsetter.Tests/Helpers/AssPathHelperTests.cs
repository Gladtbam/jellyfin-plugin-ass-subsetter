using System;
using System.IO;
using Jellyfin.Plugin.AssSubsetter.Helpers;
using Xunit;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Helpers;

public sealed class AssPathHelperTests : IDisposable
{
    private readonly string _tempPath = Path.Join(Path.GetTempPath(), "AssPathHelperTests_" + Path.GetRandomFileName());

    public AssPathHelperTests()
    {
        Directory.CreateDirectory(_tempPath);
    }

    [Fact]
    public void GetAllOriginalAssPaths_ShouldReturnAllMatchingAssFilesExceptSubsettedFiles()
    {
        string videoPath = Path.Join(_tempPath, "episode.mkv");
        string defaultPath = CreateFile("episode.ass");
        string zhPath = CreateFile("episode.zh.ass");
        string enPath = CreateFile("episode.en.ass");
        CreateFile("episode.subsetted.ass");
        CreateFile("different.ass");

        string[] result = AssPathHelper.GetAllOriginalAssPaths(videoPath);

        Assert.Equal(3, result.Length);
        Assert.Contains(defaultPath, result);
        Assert.Contains(zhPath, result);
        Assert.Contains(enPath, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void GetAllOriginalAssPaths_ShouldReturnEmpty_WhenVideoPathIsMissing(string? videoPath)
    {
        Assert.Empty(AssPathHelper.GetAllOriginalAssPaths(videoPath));
    }

    public void Dispose()
    {
        Directory.Delete(_tempPath, true);
    }

    private string CreateFile(string fileName)
    {
        string path = Path.Join(_tempPath, fileName);
        File.WriteAllText(path, "subtitle");
        return path;
    }
}
