using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
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
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Middleware;

[Collection("PluginInstance")]
public sealed class SubtitleInterceptorMiddlewareTests : IDisposable
{
    private readonly string _tempDataPath;
    private readonly Mock<SubtitleCacheService> _mockCacheService;
    private readonly Mock<ILibraryManager> _mockLibraryManager;
    private readonly Mock<IMediaSourceManager> _mockMediaSourceManager;
    private bool _nextCalled;
    private readonly RequestDelegate _nextDelegate;
    private readonly Guid _userId = Guid.NewGuid();
    private bool _hostAllowsRequest = true;

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

    [Fact]
    public async Task InvokeAsync_DoesNotUseNativeFallbackWhenCacheReturnsEmptyPath()
    {
        Guid itemId = Guid.NewGuid();
        string source = Path.Join(_tempDataPath, "source.ass");
        await File.WriteAllTextAsync(source, "source", TestContext.Current.CancellationToken);
        var video = new Video { Id = itemId };
        _mockLibraryManager.Setup(m => m.GetItemById<Video>(itemId, It.IsAny<Guid>())).Returns(video);
        _mockMediaSourceManager.Setup(m => m.GetMediaSource(video, "source", null, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaSourceInfo
            {
                Id = "source",
                MediaStreams = [new MediaStream { Type = MediaStreamType.Subtitle, Index = 0, IsExternal = true, Path = source }]
            });
        _mockCacheService.Setup(s => s.GetOrGenerateSubtitleAsync(itemId, source, It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SubtitleResult(string.Empty, "text/x-ssa", false));
        var (context, capture) = CreateFileResultContext();
        context.Request.Path = $"/Videos/{itemId:N}/source/Subtitles/0/Stream.ass";
        await CreateMiddleware().InvokeAsync(context);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.False(_nextCalled);
        Assert.Null(capture.Result);
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, "")]
    [InlineData(false, "/")]
    [InlineData(true, "/")]
    public async Task InvokeAsync_ShouldUseRequestedMediaSourceAndSubtitleIndex(bool includeStartPositionTicks, string trailingSlash)
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
        _mockLibraryManager.Setup(m => m.GetItemById<Video>(itemId, It.IsAny<Guid>())).Returns(video);
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
        context.Request.Path += trailingSlash;
        context.Request.Headers.Remove("Authorization");

        await middleware.InvokeAsync(context);

        Assert.False(_nextCalled);
        _mockLibraryManager.Verify(m => m.GetItemById<Video>(itemId, Guid.Empty), Times.Once);
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
        _mockLibraryManager.Setup(m => m.GetItemById<Video>(itemId, It.IsAny<Guid>())).Returns(video);
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

        var (context, _) = CreateFileResultContext();
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
        .Callback((LogLevel l, EventId e, object v, Exception ex, object f) => throw new InvalidOperationException("Middleware threw: " + ex, ex));

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
        _mockLibraryManager.Setup(m => m.GetItemById<Video>(itemId, It.IsAny<Guid>())).Returns(video);
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

    private (DefaultHttpContext Context, FileResultCapture Capture) CreateFileResultContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Headers.Authorization = "valid-user";
        var capture = new FileResultCapture();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization(options => options.DefaultPolicy = new AuthorizationPolicyBuilder("Jellyfin-Test")
            .RequireAuthenticatedUser()
            .RequireAssertion(_ => _hostAllowsRequest)
            .Build());
        services.AddSingleton<IPolicyEvaluator, PolicyEvaluator>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, AuthorizationMiddlewareResultHandler>();
        var authentication = new Mock<IAuthenticationService>();
        authentication.Setup(x => x.AuthenticateAsync(It.IsAny<HttpContext>(), "Jellyfin-Test"))
            .ReturnsAsync((HttpContext request, string _) =>
            {
                bool apiKey = request.Request.Query["ApiKey"] == "redacted";
                if (!apiKey && request.Request.Headers.Authorization != "valid-user")
                {
                    return string.IsNullOrEmpty(request.Request.Headers.Authorization)
                        ? AuthenticateResult.NoResult()
                        : AuthenticateResult.Fail("Invalid credentials");
                }

                var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim("Jellyfin-UserId", (apiKey ? Guid.Empty : _userId).ToString("N")),
                    new Claim("Jellyfin-IsApiKey", apiKey.ToString())
                }, "Jellyfin-Test"));
                return AuthenticateResult.Success(new AuthenticationTicket(principal, "Jellyfin-Test"));
            });
        authentication.Setup(x => x.ChallengeAsync(It.IsAny<HttpContext>(), "Jellyfin-Test", It.IsAny<AuthenticationProperties>()))
            .Callback<HttpContext, string, AuthenticationProperties>((request, _, _) => request.Response.StatusCode = 401)
            .Returns(Task.CompletedTask);
        authentication.Setup(x => x.ForbidAsync(It.IsAny<HttpContext>(), "Jellyfin-Test", It.IsAny<AuthenticationProperties>()))
            .Callback<HttpContext, string, AuthenticationProperties>((request, _, _) => request.Response.StatusCode = 403)
            .Returns(Task.CompletedTask);
        services.AddSingleton(authentication.Object);
        var mockExecutor = new Mock<IActionResultExecutor<PhysicalFileResult>>();
        mockExecutor
            .Setup(x => x.ExecuteAsync(It.IsAny<ActionContext>(), It.IsAny<PhysicalFileResult>()))
            .Callback<ActionContext, PhysicalFileResult>((_, result) =>
            {
                capture.Result = result;
                context.Response.ContentType = result.ContentType;
            })
            .Returns(Task.CompletedTask);
        services.AddSingleton(mockExecutor.Object);
        context.RequestServices = services.BuildServiceProvider();
        return (context, capture);
    }

    [Theory]
    [InlineData("", "GET")]
    [InlineData("invalid-token", "GET")]
    [InlineData("", "HEAD")]
    [InlineData("invalid-token", "HEAD")]
    public async Task InvokeAsync_RejectsUnauthenticatedRequestsBeforeLookingUpMedia(string credential, string method)
    {
        var (context, capture) = CreateFileResultContext();
        context.Request.Headers.Authorization = credential;
        context.Request.Method = method;
        context.Request.Path = $"/Videos/{Guid.NewGuid():N}/source/Subtitles/0/Stream.ass";
        // A prepopulated principal must not turn a failed host authentication into success.
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-IsApiKey", "True") }, "untrusted"));

        await CreateMiddleware().InvokeAsync(context);

        Assert.Equal(401, context.Response.StatusCode);
        Assert.False(_nextCalled);
        Assert.Null(capture.Result);
        _mockLibraryManager.VerifyNoOtherCalls();
        _mockMediaSourceManager.VerifyNoOtherCalls();
        _mockCacheService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task InvokeAsync_HonorsHostPolicyDenialBeforeLookingUpMedia()
    {
        _hostAllowsRequest = false;
        var (context, capture) = CreateFileResultContext();
        context.Request.Path = $"/Videos/{Guid.NewGuid():N}/source/Subtitles/0/Stream.ass";

        await CreateMiddleware().InvokeAsync(context);

        Assert.Equal(403, context.Response.StatusCode);
        Assert.False(_nextCalled);
        Assert.Null(capture.Result);
        _mockLibraryManager.VerifyNoOtherCalls();
        _mockCacheService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task InvokeAsync_RejectsItemHiddenFromAuthenticatedUserWithoutFallback()
    {
        var itemId = Guid.NewGuid();
        var (context, capture) = CreateFileResultContext();
        context.Request.Path = $"/Videos/{itemId:N}/source/Subtitles/0/Stream.ass";
        _mockLibraryManager.Setup(x => x.GetItemById<Video>(itemId, _userId)).Returns((Video?)null);

        await CreateMiddleware().InvokeAsync(context);

        Assert.Equal(404, context.Response.StatusCode);
        Assert.False(_nextCalled);
        Assert.Null(capture.Result);
        _mockLibraryManager.Verify(x => x.GetItemById<Video>(itemId, _userId), Times.Once);
        _mockMediaSourceManager.VerifyNoOtherCalls();
        _mockCacheService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task InvokeAsync_RejectsDifferentLegacyQueryItemBeforeFallback()
    {
        var itemId = Guid.NewGuid();
        var (context, capture) = CreateFileResultContext();
        context.Request.Path = $"/Videos/{itemId:N}/source/Subtitles/0/Stream.ass";
        context.Request.QueryString = new QueryString($"?itemId={Guid.NewGuid():N}");
        _mockLibraryManager.Setup(x => x.GetItemById<Video>(itemId, _userId)).Returns(new Video { Id = itemId });

        await CreateMiddleware().InvokeAsync(context);

        Assert.Equal(400, context.Response.StatusCode);
        Assert.False(_nextCalled);
        Assert.Null(capture.Result);
        _mockMediaSourceManager.VerifyNoOtherCalls();
        _mockCacheService.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("GetSubtitle", "GET", false)]
    [InlineData("GetSubtitleWithTicks", "GET", false)]
    [InlineData("GetSubtitle", "HEAD", false)]
    [InlineData("GetSubtitle", "GET", true)]
    [InlineData("GetSubtitleWithTicks", "HEAD", true)]
    public async Task InvokeAsync_TrailingSlashCannotBypassAuthentication(string action, string method, bool useSelectedRoute)
    {
        var (context, capture) = CreateFileResultContext();
        context.Request.Headers.Remove("Authorization");
        context.Request.Method = method;
        var itemId = Guid.NewGuid();
        string ticks = action == "GetSubtitleWithTicks" ? "/0" : string.Empty;
        context.Request.Path = $"/Videos/{itemId:N}/source/Subtitles/0{ticks}/Stream.ass/";
        if (useSelectedRoute)
        {
            // A Guid form accepted by MVC but outside the old hand-written regex.
            context.Request.Path = $"/Videos/{itemId:B}/source/Subtitles/+0{ticks}/Stream.ass/";
            context.Request.RouteValues["controller"] = "Subtitle";
            context.Request.RouteValues["action"] = action;
            context.Request.RouteValues["routeFormat"] = "ass";
            context.Request.RouteValues["routeItemId"] = itemId.ToString("B");
            context.Request.RouteValues["routeMediaSourceId"] = "source";
            context.Request.RouteValues["routeIndex"] = "+0";
        }

        await CreateMiddleware().InvokeAsync(context);

        Assert.Equal(401, context.Response.StatusCode);
        Assert.False(_nextCalled);
        Assert.Null(capture.Result);
        _mockLibraryManager.VerifyNoOtherCalls();
        _mockMediaSourceManager.VerifyNoOtherCalls();
        _mockCacheService.VerifyNoOtherCalls();
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
