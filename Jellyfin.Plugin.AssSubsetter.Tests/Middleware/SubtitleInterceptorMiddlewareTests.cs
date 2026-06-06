using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Middleware;
using Jellyfin.Plugin.AssSubsetter.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Middleware;

public class SubtitleInterceptorMiddlewareTests : IDisposable
{
    private readonly string _tempDataPath;
    private readonly Mock<SubtitleCacheService> _mockCacheService;
    private readonly Mock<ILibraryManager> _mockLibraryManager;
    private bool _nextCalled;
    private readonly RequestDelegate _nextDelegate;

    public SubtitleInterceptorMiddlewareTests()
    {
        _tempDataPath = Path.Join(Path.GetTempPath(), "MiddlewareTests_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDataPath);

        _mockLibraryManager = new Mock<ILibraryManager>();

        _mockCacheService = new Mock<SubtitleCacheService>(
            null!, null!, _tempDataPath, new NullLogger<SubtitleCacheService>());

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
        var middleware = new SubtitleInterceptorMiddleware(
            _nextDelegate, _mockCacheService.Object, _mockLibraryManager.Object, new NullLogger<SubtitleInterceptorMiddleware>());

        var context = new DefaultHttpContext();
        context.Request.Path = "/System/Info";

        await middleware.InvokeAsync(context);

        Assert.True(_nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_ShouldInterceptAndSetContentType_WhenRouteMatchesAndFileExists()
    {
        var middleware = new SubtitleInterceptorMiddleware(
            _nextDelegate, _mockCacheService.Object, _mockLibraryManager.Object, new NullLogger<SubtitleInterceptorMiddleware>());

        var itemId = Guid.NewGuid();
        var context = new DefaultHttpContext();

        context.Request.Path = $"/Videos/{itemId:N}/stream/Subtitles/2/Stream.ass";

        string videoPath = Path.Join(_tempDataPath, "movie.mkv");
        string originalAssPath = Path.Join(_tempDataPath, "movie.ass");
        string cachedAssPath = Path.Join(_tempDataPath, "movie_cached.ass");

        await File.WriteAllTextAsync(videoPath, "dummy video", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(originalAssPath, "dummy original sub", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(cachedAssPath, "dummy subset sub", TestContext.Current.CancellationToken);

        var video = new Video { Id = itemId, Path = videoPath };
        _mockLibraryManager.Setup(m => m.GetItemById(itemId)).Returns(video);

        _mockCacheService
            .Setup(s => s.GetOrGenerateSubtitleAsync(itemId, originalAssPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(cachedAssPath);

        await middleware.InvokeAsync(context);

        Assert.False(_nextCalled, "Middleware should short-circuit the pipeline.");
        Assert.Equal("text/x-ssa", context.Response.ContentType);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDataPath))
        {
            Directory.Delete(_tempDataPath, true);
        }
    }
}
