using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.Middleware;

/// <summary>
/// ASP.NET Core middleware to intercept Jellyfin ASS subtitle streams and replace them with subsetted fonts version.
/// </summary>
public class SubtitleInterceptorMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<SubtitleInterceptorMiddleware> _logger;
    private readonly SubtitleCacheService _cacheService;
    private readonly ILibraryManager _libraryManager;
    private readonly IHostApplicationLifetime _appLifetime;

    private static readonly Regex _subtitleRouteRegex = new(
        @"^/Videos/(?<ItemId>[a-f0-9]{32}|[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})/.*/Subtitles/\d+(?:/\d+)?/Stream\.ass$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleInterceptorMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in pipeline.</param>
    /// <param name="cacheService">The subtitle cache service.</param>
    /// <param name="libraryManager">The library manager to locate physical files.</param>
    /// <param name="appLifetime">The application lifetime for background task cancellation.</param>
    /// <param name="logger">The logger instance.</param>
    public SubtitleInterceptorMiddleware(
        RequestDelegate next,
        SubtitleCacheService cacheService,
        ILibraryManager libraryManager,
        IHostApplicationLifetime appLifetime,
        ILogger<SubtitleInterceptorMiddleware> logger)
    {
        _next = next;
        _cacheService = cacheService;
        _libraryManager = libraryManager;
        _appLifetime = appLifetime;
        _logger = logger;
    }

    /// <summary>
    /// Invokes the middleware to process requests.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Inputs are robustly validated using Guid.TryParse and verified against internal DB.")]
    public async Task InvokeAsync(HttpContext context)
    {
        string path = context.Request.Path.Value ?? string.Empty;
        var match = _subtitleRouteRegex.Match(path);

        if (match.Success)
        {
            string itemIdString = match.Groups["ItemId"].Value;

            if (Guid.TryParse(itemIdString, out Guid itemId))
            {
                _logger.LogDebug("[AssSubsetter] Intercepted ASS subtitle request for item: {ItemId}", itemId);

                try
                {
                    if (_libraryManager.GetItemById(itemId) is Video video)
                    {
                        string originalAssPath = AssPathHelper.GetOriginalAssPath(video.Path);

                        if (!string.IsNullOrEmpty(originalAssPath) && File.Exists(originalAssPath))
                        {
                            var result = await _cacheService.GetOrGenerateSubtitleAsync(
                                itemId,
                                originalAssPath,
                                video.Width,
                                video.Height,
                                context.RequestAborted).ConfigureAwait(false);

                            string finalPath = result.Path;
                            string contentType = result.ContentType;

                            if (!string.IsNullOrEmpty(finalPath) && File.Exists(finalPath))
                            {
                                var corsService = context.RequestServices.GetService(typeof(ICorsService)) as ICorsService;
                                var corsPolicyProvider = context.RequestServices.GetService(typeof(ICorsPolicyProvider)) as ICorsPolicyProvider;

                                if (corsService != null && corsPolicyProvider != null)
                                {
                                    var policy = await corsPolicyProvider.GetPolicyAsync(context, null).ConfigureAwait(false);
                                    if (policy != null)
                                    {
                                        var corsResult = corsService.EvaluatePolicy(context, policy);
                                        corsService.ApplyResult(corsResult, context.Response);
                                    }
                                }

                                var fileResult = new PhysicalFileResult(finalPath, contentType)
                                {
                                    EnableRangeProcessing = true
                                };

                                var actionContext = new ActionContext(
                                    context,
                                    new RouteData(),
                                    new ActionDescriptor());

                                await fileResult.ExecuteResultAsync(actionContext).ConfigureAwait(false);
                                return;
                            }
                        }
                        else
                        {
                            _logger.LogWarning("[AssSubsetter] Original ASS path not found or missing for item: {ItemId}", itemId);
                        }
                    }
                }

                // codeql[cs/catch-of-all-exceptions] Justification: Middleware must not crash the request pipeline; fallback to native processing is required on any failure.
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[AssSubsetter] Error occurred while intercepting subtitle stream. Falling back to native pipeline.");
                }
            }
        }

        await _next(context).ConfigureAwait(false);
    }
}
