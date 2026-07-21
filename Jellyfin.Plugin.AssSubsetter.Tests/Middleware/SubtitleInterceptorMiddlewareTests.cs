using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Middleware;
using Jellyfin.Plugin.AssSubsetter.Models;
using Jellyfin.Plugin.AssSubsetter.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Middleware;

[Collection("PluginInstance")]
public class SubtitleInterceptorMiddlewareTests : IDisposable
{
    private readonly string _tempDataPath;
    private readonly Mock<SubtitleCacheService> _mockCacheService;
    private readonly Mock<ILibraryManager> _mockLibraryManager;
    private readonly Mock<IMediaSourceManager> _mockMediaSourceManager;
    private bool _nextCalled;
    private readonly RequestDelegate _nextDelegate;

    public SubtitleInterceptorMiddlewareTests()
    {
        _tempDataPath = Path.Join(Path.GetTempPath(), "MiddlewareTests_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDataPath);

        _mockLibraryManager = new Mock<ILibraryManager>();
        _mockMediaSourceManager = new Mock<IMediaSourceManager>();

        _mockCacheService = new Mock<SubtitleCacheService>(
            null!, null!, null!, _tempDataPath, new NullLogger<SubtitleCacheService>());

        _nextCalled = false;
        _nextDelegate = (HttpContext context) =>
        {
            _nextCalled = true;
            return Task.CompletedTask;
        };
    }

    [Fact]
    public async Task InvokeAsync_ShouldCallNext_WhenRouteDoesNotMatch()
    {
        var middleware = CreateMiddleware();

        var context = new DefaultHttpContext();
        context.Request.Path = "/System/Info";

        await middleware.InvokeAsync(context);

        Assert.True(_nextCalled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvokeAsync_ShouldUseRequestedMediaSourceAndSubtitleIndex(bool includeStartPositionTicks)
    {
        var middleware = CreateMiddleware();
        var itemId = Guid.NewGuid();
        const string mediaSourceId = "requested-source";
        const int requestedIndex = 3;
        string requestedPath = Path.Join(_tempDataPath, "requested.ass");
        string otherPath = Path.Join(_tempDataPath, "other.ass");
        string cachedPath = Path.Join(_tempDataPath, "cached.ass");
        await File.WriteAllTextAsync(requestedPath, "requested", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(otherPath, "other", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(cachedPath, "cached", TestContext.Current.CancellationToken);

        var video = new Video { Id = itemId, Path = Path.Join(_tempDataPath, "movie.mkv") };
        _mockLibraryManager.Setup(m => m.GetItemById(itemId)).Returns(video);
        _mockMediaSourceManager
            .Setup(m => m.GetMediaSource(video, mediaSourceId, null, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaSourceInfo
            {
                Id = mediaSourceId,
                MediaStreams = new List<MediaStream>
                {
                    new() { Type = MediaStreamType.Subtitle, Index = 1, IsExternal = true, Path = otherPath },
                    new() { Type = MediaStreamType.Subtitle, Index = requestedIndex, IsExternal = true, Path = requestedPath }
                }
            });
        _mockCacheService
            .Setup(s => s.GetOrGenerateSubtitleAsync(itemId, requestedPath, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubtitleResult(cachedPath, "text/x-ssa", true));

        var (context, _) = CreateFileResultContext();
        context.Request.Path = includeStartPositionTicks
            ? $"/Videos/{itemId:N}/{mediaSourceId}/Subtitles/{requestedIndex}/0/Stream.ass"
            : $"/Videos/{itemId:N}/{mediaSourceId}/Subtitles/{requestedIndex}/Stream.ass";
        context.Request.QueryString = new QueryString("?ApiKey=redacted");

        await middleware.InvokeAsync(context);

        Assert.False(_nextCalled);
        _mockCacheService.Verify(
            s => s.GetOrGenerateSubtitleAsync(itemId, requestedPath, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData(MediaStreamType.Audio, true, ".ass", 3, true)]
    [InlineData(MediaStreamType.Subtitle, false, ".ass", 3, true)]
    [InlineData(MediaStreamType.Subtitle, true, ".srt", 3, true)]
    [InlineData(MediaStreamType.Subtitle, true, ".ass", 2, true)]
    [InlineData(MediaStreamType.Subtitle, true, ".ass", 3, false)]
    public async Task InvokeAsync_ShouldCallNext_WhenRequestedStreamIsNotUsable(
        MediaStreamType streamType,
        bool isExternal,
        string extension,
        int streamIndex,
        bool createFile)
    {
        var middleware = CreateMiddleware();
        var itemId = Guid.NewGuid();
        const string mediaSourceId = "source";
        string subtitlePath = Path.Join(_tempDataPath, "subtitle" + extension);
        if (createFile)
        {
            await File.WriteAllTextAsync(subtitlePath, "subtitle", TestContext.Current.CancellationToken);
        }

        var video = new Video { Id = itemId, Path = Path.Join(_tempDataPath, "movie.mkv") };
        _mockLibraryManager.Setup(m => m.GetItemById(itemId)).Returns(video);
        _mockMediaSourceManager
            .Setup(m => m.GetMediaSource(video, mediaSourceId, null, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaSourceInfo
            {
                Id = mediaSourceId,
                MediaStreams = new List<MediaStream>
                {
                    new() { Type = streamType, Index = streamIndex, IsExternal = isExternal, Path = subtitlePath }
                }
            });

        var context = new DefaultHttpContext();
        context.Request.Path = $"/Videos/{itemId:N}/{mediaSourceId}/Subtitles/3/Stream.ass";

        await middleware.InvokeAsync(context);

        Assert.True(_nextCalled);
        _mockCacheService.Verify(
            s => s.GetOrGenerateSubtitleAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("text/x-ssa", ".ass")]
    [InlineData("video/x-matroska", ".mks")]
    public async Task InvokeAsync_ShouldInterceptAndSetContentType_WhenRouteMatchesAndFileExists(string contentType, string extension)
    {
        var mockLogger = new Mock<ILogger<SubtitleInterceptorMiddleware>>();
        mockLogger.Setup(x => x.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception>(),
            (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()))
        .Callback((LogLevel l, EventId e, object v, Exception ex, object f) => throw new Exception("Middleware threw: " + ex));

        var middleware = CreateMiddleware(mockLogger.Object);

        var itemId = Guid.NewGuid();
        var (context, capture) = CreateFileResultContext();
        const string mediaSourceId = "stream";
        context.Request.Path = $"/Videos/{itemId:N}/{mediaSourceId}/Subtitles/2/Stream.ass";

        string videoPath = Path.Join(_tempDataPath, "movie.mkv");
        string originalAssPath = Path.Join(_tempDataPath, "movie.ass");
        string cachedAssPath = Path.Join(_tempDataPath, "movie_cached" + extension);

        await File.WriteAllTextAsync(videoPath, "dummy video", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(originalAssPath, "dummy original sub", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(cachedAssPath, "dummy subset sub", TestContext.Current.CancellationToken);

        var video = new Video { Id = itemId, Path = videoPath };
        _mockLibraryManager.Setup(m => m.GetItemById(itemId)).Returns(video);
        _mockMediaSourceManager
            .Setup(m => m.GetMediaSource(video, mediaSourceId, null, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaSourceInfo
            {
                Id = mediaSourceId,
                MediaStreams = new List<MediaStream>
                {
                    new() { Type = MediaStreamType.Subtitle, Index = 2, IsExternal = true, Path = originalAssPath }
                }
            });

        _mockCacheService
            .Setup(s => s.GetOrGenerateSubtitleAsync(itemId, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubtitleResult(cachedAssPath, contentType, true));

        await middleware.InvokeAsync(context);

        Assert.False(_nextCalled, "Middleware should short-circuit the pipeline.");
        Assert.Equal(contentType, context.Response.ContentType);
        Assert.NotNull(capture.Result);
        Assert.True(capture.Result.EnableRangeProcessing);
    }

    private SubtitleInterceptorMiddleware CreateMiddleware(ILogger<SubtitleInterceptorMiddleware>? logger = null)
    {
        return new SubtitleInterceptorMiddleware(
            _nextDelegate,
            _mockCacheService.Object,
            _mockLibraryManager.Object,
            new SubtitleStreamResolver(_mockMediaSourceManager.Object),
            new Mock<IHostApplicationLifetime>().Object,
            logger ?? new NullLogger<SubtitleInterceptorMiddleware>());
    }

    private static (DefaultHttpContext Context, FileResultCapture Capture) CreateFileResultContext()
    {
        var context = new DefaultHttpContext();
        var capture = new FileResultCapture();
        var mockServiceProvider = new Mock<IServiceProvider>();
        var mockExecutor = new Mock<IActionResultExecutor<PhysicalFileResult>>();
        mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<ActionContext>(), It.IsAny<PhysicalFileResult>()))
            .Callback<ActionContext, PhysicalFileResult>((_, result) =>
            {
                capture.Result = result;
                context.Response.ContentType = result.ContentType;
            })
            .Returns(Task.CompletedTask);
        mockServiceProvider
            .Setup(x => x.GetService(typeof(IActionResultExecutor<PhysicalFileResult>)))
            .Returns(mockExecutor.Object);
        context.RequestServices = mockServiceProvider.Object;
        return (context, capture);
    }

    private sealed class FileResultCapture
    {
        public PhysicalFileResult? Result { get; set; }
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDataPath))
        {
            Directory.Delete(_tempDataPath, true);
        }
    }
}
