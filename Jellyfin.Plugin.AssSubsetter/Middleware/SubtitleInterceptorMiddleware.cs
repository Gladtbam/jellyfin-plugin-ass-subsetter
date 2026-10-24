using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AssSubsetter.Middleware;

/// <summary>
///     ASP.NET Core middleware to intercept Jellyfin ASS subtitle streams and replace them with subsetted fonts version.
/// </summary>
public class SubtitleInterceptorMiddleware
{
    private static readonly Regex _subtitleRouteRegex = new(
        @"^/Videos/(?<ItemId>[a-f0-9]{32}|[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})/(?<MediaSourceId>[^/]+)/Subtitles/(?<SubtitleIndex>\d+)(?:/\d+)?/Stream\.ass/?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IHostApplicationLifetime _appLifetime;
    private readonly SubtitleCacheService _cacheService;
    private readonly ILibraryManager _libraryManager;
    private readonly SubtitleStreamResolver _subtitleStreamResolver;
    private readonly ILogger<SubtitleInterceptorMiddleware> _logger;
    private readonly RequestDelegate _next;

    /// <summary>
    ///     Initializes a new instance of the <see cref="SubtitleInterceptorMiddleware" /> class.
    /// </summary>
    /// <param name="next">The next middleware in pipeline.</param>
    /// <param name="cacheService">The subtitle cache service.</param>
    /// <param name="libraryManager">The library manager to locate physical files.</param>
    /// <param name="subtitleStreamResolver">The service that resolves the requested subtitle stream.</param>
    /// <param name="appLifetime">The application lifetime for background task cancellation.</param>
    /// <param name="logger">The logger instance.</param>
    public SubtitleInterceptorMiddleware(
        RequestDelegate next,
        SubtitleCacheService cacheService,
        ILibraryManager libraryManager,
        SubtitleStreamResolver subtitleStreamResolver,
        IHostApplicationLifetime appLifetime,
        ILogger<SubtitleInterceptorMiddleware> logger)
    {
        _next = next;
        _cacheService = cacheService;
        _libraryManager = libraryManager;
        _subtitleStreamResolver = subtitleStreamResolver;
        _appLifetime = appLifetime;
        _logger = logger;
    }

    /// <summary>
    ///     Invokes the middleware to process requests.
    /// </summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Inputs are robustly validated using Guid.TryParse and verified against internal DB.")]
    public async Task InvokeAsync(HttpContext context)
    {
        string path = context.Request.Path.Value ?? string.Empty;
        var match = _subtitleRouteRegex.Match(path);
        var route = context.Request.RouteValues;
        string? action = Convert.ToString(route["action"], CultureInfo.InvariantCulture);
        bool selectedSubtitleAction = string.Equals(Convert.ToString(route["controller"], CultureInfo.InvariantCulture), "Subtitle", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(action, "GetSubtitle", StringComparison.OrdinalIgnoreCase)
                || string.Equals(action, "GetSubtitleWithTicks", StringComparison.OrdinalIgnoreCase))
            && string.Equals(Convert.ToString(route["routeFormat"], CultureInfo.InvariantCulture), "ass", StringComparison.OrdinalIgnoreCase);

        // MVC's selected route is authoritative: do not independently reject path forms
        // accepted by host routing, such as trailing slashes or alternate Guid formats.
        if ((selectedSubtitleAction || match.Success) && (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)))
        {
            string? itemIdString = selectedSubtitleAction ? Convert.ToString(route["routeItemId"], CultureInfo.InvariantCulture) : match.Groups["ItemId"].Value;
            string mediaSourceId = (selectedSubtitleAction ? Convert.ToString(route["routeMediaSourceId"], CultureInfo.InvariantCulture) : match.Groups["MediaSourceId"].Value) ?? string.Empty;
            string? subtitleIndexString = selectedSubtitleAction ? Convert.ToString(route["routeIndex"], CultureInfo.InvariantCulture) : match.Groups["SubtitleIndex"].Value;

            if (!Guid.TryParse(itemIdString, out _) || !int.TryParse(subtitleIndexString, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            if (Guid.TryParse(itemIdString, out Guid itemId)
                && int.TryParse(subtitleIndexString, NumberStyles.Integer, CultureInfo.InvariantCulture, out int subtitleIndex))
            {
                _logger.LogDebug("[AssSubsetter] Intercepted ASS subtitle request for item: {ItemId}", itemId);

                // Keep security checks outside the conversion fallback: a denied request
                // must never reach the native subtitle action (which permits anonymous streams).
                var policyProvider = context.RequestServices.GetRequiredService<IAuthorizationPolicyProvider>();
                var evaluator = context.RequestServices.GetRequiredService<IPolicyEvaluator>();
                var policy = await policyProvider.GetDefaultPolicyAsync().ConfigureAwait(false);
                var authentication = await evaluator.AuthenticateAsync(policy, context).ConfigureAwait(false);
                var authorization = await evaluator.AuthorizeAsync(policy, authentication, context, context).ConfigureAwait(false);
                if (!authentication.Succeeded || !authorization.Succeeded)
                {
                    var handler = context.RequestServices.GetRequiredService<IAuthorizationMiddlewareResultHandler>();
                    await handler.HandleAsync(
                        _ => Task.CompletedTask,
                        context,
                        policy,
                        authentication.Succeeded ? authorization : PolicyAuthorizationResult.Challenge()).ConfigureAwait(false);
                    return;
                }

                // These claim names are the Jellyfin 10.11.9 authentication contract.
                // API keys have no user and are unrestricted by Jellyfin's default policy.
                var principal = authentication.Principal!;
                bool isApiKey = bool.TryParse(principal.Claims.FirstOrDefault(c => string.Equals(c.Type, "Jellyfin-IsApiKey", StringComparison.OrdinalIgnoreCase))?.Value, out bool apiKey) && apiKey;
                Guid userId = Guid.Empty;
                if (!isApiKey && (!Guid.TryParse(principal.Claims.FirstOrDefault(c => string.Equals(c.Type, "Jellyfin-UserId", StringComparison.OrdinalIgnoreCase))?.Value, out userId) || userId == Guid.Empty))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                var video = _libraryManager.GetItemById<Video>(itemId, userId);
                if (video is null)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                // Jellyfin's legacy query parameter overrides the route item inside its
                // action. Never fall back to a different, unchecked item.
                if (context.Request.Query.TryGetValue("itemId", out var queryItemId)
                    && (!Guid.TryParse(queryItemId.ToString(), out var requestedItemId) || requestedItemId != itemId))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                try
                {
                    {
                        string? originalAssPath = await _subtitleStreamResolver.ResolveExternalAssPathAsync(
                            video,
                            mediaSourceId,
                            subtitleIndex,
                            context.RequestAborted).ConfigureAwait(false);

                        if (originalAssPath is not null)
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
                                    var corsPolicy = await corsPolicyProvider.GetPolicyAsync(context, null).ConfigureAwait(false);
                                    if (corsPolicy != null)
                                    {
                                        var corsResult = corsService.EvaluatePolicy(context, corsPolicy);
                                        corsService.ApplyResult(corsResult, context.Response);
                                    }
                                }

                                var fileResult = new PhysicalFileResult(finalPath, contentType) { EnableRangeProcessing = true };

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
