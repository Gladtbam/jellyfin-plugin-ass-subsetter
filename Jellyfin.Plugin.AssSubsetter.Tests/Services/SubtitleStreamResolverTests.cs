using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Services;

public sealed class SubtitleStreamResolverTests : IDisposable
{
    private readonly string _tempPath = Path.Join(Path.GetTempPath(), "SubtitleStreamResolverTests_" + Path.GetRandomFileName());

    public SubtitleStreamResolverTests()
    {
        Directory.CreateDirectory(_tempPath);
    }

    [Fact]
    public async Task ResolveExternalAssPathAsync_ShouldReturnRequestedExternalAssPath()
    {
        var mediaSourceManager = new Mock<IMediaSourceManager>();
        var resolver = new SubtitleStreamResolver(mediaSourceManager.Object);
        var video = new Video { Id = Guid.NewGuid() };
        const string mediaSourceId = "requested-source";
        const int requestedIndex = 3;
        string otherPath = await CreateFileAsync("other.ass");
        string requestedPath = await CreateFileAsync("requested.ass");
        mediaSourceManager
            .Setup(manager => manager.GetMediaSource(video, mediaSourceId, null, false, TestContext.Current.CancellationToken))
            .ReturnsAsync(CreateMediaSource(
                new MediaStream { Type = MediaStreamType.Subtitle, Index = 1, IsExternal = true, Path = otherPath },
                new MediaStream { Type = MediaStreamType.Subtitle, Index = requestedIndex, IsExternal = true, Path = requestedPath }));

        string? result = await resolver.ResolveExternalAssPathAsync(
            video, mediaSourceId, requestedIndex, TestContext.Current.CancellationToken);

        Assert.Equal(requestedPath, result);
        mediaSourceManager.Verify(
            manager => manager.GetMediaSource(video, mediaSourceId, null, false, TestContext.Current.CancellationToken),
            Times.Once);
    }

    [Theory]
    [InlineData(MediaStreamType.Audio, true, ".ass", 3, true)]
    [InlineData(MediaStreamType.Subtitle, false, ".ass", 3, true)]
    [InlineData(MediaStreamType.Subtitle, true, ".srt", 3, true)]
    [InlineData(MediaStreamType.Subtitle, true, ".ass", 2, true)]
    [InlineData(MediaStreamType.Subtitle, true, ".ass", 3, false)]
    public async Task ResolveExternalAssPathAsync_ShouldReturnNull_WhenRequestedStreamIsNotUsable(
        MediaStreamType streamType,
        bool isExternal,
        string extension,
        int streamIndex,
        bool createFile)
    {
        var mediaSourceManager = new Mock<IMediaSourceManager>();
        var resolver = new SubtitleStreamResolver(mediaSourceManager.Object);
        var video = new Video { Id = Guid.NewGuid() };
        const string mediaSourceId = "source";
        string path = Path.Join(_tempPath, "subtitle" + extension);
        if (createFile)
        {
            await File.WriteAllTextAsync(path, "subtitle", TestContext.Current.CancellationToken);
        }

        mediaSourceManager
            .Setup(manager => manager.GetMediaSource(video, mediaSourceId, null, false, TestContext.Current.CancellationToken))
            .ReturnsAsync(CreateMediaSource(
                new MediaStream { Type = streamType, Index = streamIndex, IsExternal = isExternal, Path = path }));

        string? result = await resolver.ResolveExternalAssPathAsync(
            video, mediaSourceId, 3, TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    public void Dispose()
    {
        Directory.Delete(_tempPath, true);
    }

    private async Task<string> CreateFileAsync(string fileName)
    {
        string path = Path.Join(_tempPath, fileName);
        await File.WriteAllTextAsync(path, "subtitle", TestContext.Current.CancellationToken);
        return path;
    }

    private static MediaSourceInfo CreateMediaSource(params MediaStream[] streams)
    {
        return new MediaSourceInfo { MediaStreams = new List<MediaStream>(streams) };
    }
}
