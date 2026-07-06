using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Middleware;
using Jellyfin.Plugin.AssSubsetter.Models;
using Jellyfin.Plugin.AssSubsetter.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
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
    private bool _nextCalled;
    private readonly RequestDelegate _nextDelegate;

    public SubtitleInterceptorMiddlewareTests()
    {
        _tempDataPath = Path.Join(Path.GetTempPath(), "MiddlewareTests_" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDataPath);

        _mockLibraryManager = new Mock<ILibraryManager>();

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
        var mockAppLifetime = new Mock<IHostApplicationLifetime>();
        var middleware = new SubtitleInterceptorMiddleware(
            _nextDelegate, _mockCacheService.Object, _mockLibraryManager.Object, mockAppLifetime.Object, new NullLogger<SubtitleInterceptorMiddleware>());

        var context = new DefaultHttpContext();
        context.Request.Path = "/System/Info";

        await middleware.InvokeAsync(context);

        Assert.True(_nextCalled);
    }

    [Fact]
    public async Task InvokeAsync_ShouldInterceptAndSetContentType_WhenRouteMatchesAndFileExists()
    {
        var mockLogger = new Mock<ILogger<SubtitleInterceptorMiddleware>>();
        mockLogger.Setup(x => x.Log(
            LogLevel.Error,
            It.IsAny<EventId>(),
            It.IsAny<It.IsAnyType>(),
            It.IsAny<Exception>(),
            (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()))
        .Callback((LogLevel l, EventId e, object v, Exception ex, object f) => throw new Exception("Middleware threw: " + ex));

        var mockAppLifetime2 = new Mock<IHostApplicationLifetime>();
        var middleware = new SubtitleInterceptorMiddleware(
            _nextDelegate, _mockCacheService.Object, _mockLibraryManager.Object, mockAppLifetime2.Object, mockLogger.Object);

        var itemId = Guid.NewGuid();
        var context = new DefaultHttpContext();
        var mockServiceProvider = new Mock<IServiceProvider>();
        var mockExecutor = new Mock<Microsoft.AspNetCore.Mvc.Infrastructure.IActionResultExecutor<Microsoft.AspNetCore.Mvc.PhysicalFileResult>>();
        mockExecutor.Setup(x => x.ExecuteAsync(It.IsAny<Microsoft.AspNetCore.Mvc.ActionContext>(), It.IsAny<Microsoft.AspNetCore.Mvc.PhysicalFileResult>()))
                    .Returns(Task.CompletedTask)
                    .Callback(() => context.Response.ContentType = "text/x-ssa"); // mimic the executor setting content type
        mockServiceProvider.Setup(x => x.GetService(typeof(Microsoft.AspNetCore.Mvc.Infrastructure.IActionResultExecutor<Microsoft.AspNetCore.Mvc.PhysicalFileResult>)))
                           .Returns(mockExecutor.Object);
        context.RequestServices = mockServiceProvider.Object;

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
            .Setup(s => s.GetOrGenerateSubtitleAsync(itemId, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubtitleResult(cachedAssPath, "text/x-ssa", true));

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
