using System;
using System.IO;
using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.Helpers;
using Xunit;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Helpers;

public sealed class SubtitleCacheFingerprintTests : IDisposable
{
    private readonly string _tempDirectory;
    private readonly string _subtitlePath;

    public SubtitleCacheFingerprintTests()
    {
        _tempDirectory = Path.Join(Path.GetTempPath(), "SubtitleCacheFingerprintTests_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDirectory);
        _subtitlePath = Path.Join(_tempDirectory, "subtitle.ass");
        File.WriteAllText(_subtitlePath, "first version");
    }

    [Fact]
    public void Create_ShouldReturnStableFileNameSafeFingerprint()
    {
        string first = SubtitleCacheFingerprint.Create(_subtitlePath, SubtitleProcessingMode.Subsetting);
        string second = SubtitleCacheFingerprint.Create(_subtitlePath, SubtitleProcessingMode.Subsetting);

        Assert.Equal(first, second);
        Assert.Matches("^[A-Za-z0-9_-]{16}$", first);
    }

    [Fact]
    public void Create_ShouldChangeWhenSourceMetadataChanges()
    {
        string first = SubtitleCacheFingerprint.Create(_subtitlePath, SubtitleProcessingMode.Subsetting);

        File.AppendAllText(_subtitlePath, " and a longer second version");
        string second = SubtitleCacheFingerprint.Create(_subtitlePath, SubtitleProcessingMode.Subsetting);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Create_ShouldChangeForSupRenderingParametersOnlyInSupMode()
    {
        string sup = SubtitleCacheFingerprint.Create(
            _subtitlePath,
            SubtitleProcessingMode.ConvertToSup,
            width: 1920,
            height: 1080,
            frameRate: 24);
        string differentWidth = SubtitleCacheFingerprint.Create(
            _subtitlePath,
            SubtitleProcessingMode.ConvertToSup,
            width: 1280,
            height: 1080,
            frameRate: 24);
        string differentFrameRate = SubtitleCacheFingerprint.Create(
            _subtitlePath,
            SubtitleProcessingMode.ConvertToSup,
            width: 1920,
            height: 1080,
            frameRate: 30);
        string assWithParameters = SubtitleCacheFingerprint.Create(
            _subtitlePath,
            SubtitleProcessingMode.Subsetting,
            width: 1280,
            height: 720,
            frameRate: 60);
        string assWithoutParameters = SubtitleCacheFingerprint.Create(
            _subtitlePath,
            SubtitleProcessingMode.Subsetting);

        Assert.NotEqual(sup, differentWidth);
        Assert.NotEqual(sup, differentFrameRate);
        Assert.Equal(assWithoutParameters, assWithParameters);
    }

    public void Dispose()
    {
        Directory.Delete(_tempDirectory, recursive: true);
        GC.SuppressFinalize(this);
    }
}
