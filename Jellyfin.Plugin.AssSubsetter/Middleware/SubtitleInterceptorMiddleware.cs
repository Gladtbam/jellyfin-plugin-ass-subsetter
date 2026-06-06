using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
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

    private static readonly Regex _subtitleRouteRegex = new(
        @"^/Videos/(?<ItemId>[a-f0-9]{32}|[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})/.*/Subtitles/\d+(?:/\d+)?/Stream\.ass$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Initializes a new instance of the <see cref="SubtitleInterceptorMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next middleware in pipeline.</param>
    /// <param name="cacheService">The subtitle cache service.</param>
    /// <param name="libraryManager">The library manager to locate physical files.</param>
    /// <param name="logger">The logger instance.</param>
    public SubtitleInterceptorMiddleware(
        RequestDelegate next,
        SubtitleCacheService cacheService,
        ILibraryManager libraryManager,
        ILogger<SubtitleInterceptorMiddleware> logger)
    {
        _next = next;
        _cacheService = cacheService;
        _libraryManager = libraryManager;
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
                        string originalAssPath = GetOriginalAssPath(video);

                        if (!string.IsNullOrEmpty(originalAssPath) && File.Exists(originalAssPath))
                        {
                            _logger.LogDebug("[AssSubsetter] Target external ASS found: {Path}", originalAssPath);

                            string finalAssPath = await _cacheService.GetOrGenerateSubtitleAsync(
                                itemId,
                                originalAssPath,
                                context.RequestAborted).ConfigureAwait(false);

                            if (!string.IsNullOrEmpty(finalAssPath) && File.Exists(finalAssPath))
                            {
                                context.Response.ContentType = "text/x-ssa";
                                await context.Response.SendFileAsync(finalAssPath, context.RequestAborted).ConfigureAwait(false);
                                return;
                            }
                        }
                        else
                        {
                            _logger.LogWarning("[AssSubsetter] Original ASS path not found or missing for item: {ItemId}", itemId);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[AssSubsetter] Error occurred while intercepting subtitle stream. Falling back to native pipeline.");
                }
            }
        }

        await _next(context).ConfigureAwait(false);
    }

    [SuppressMessage("Security", "CA3003:Review code for file path injection vulnerabilities", Justification = "Path is determined solely from trusted database objects.")]
    private static string GetOriginalAssPath(Video video)
    {
        if (string.IsNullOrEmpty(video.Path))
        {
            return string.Empty;
        }

        string videoDir = Path.GetDirectoryName(video.Path) ?? string.Empty;
        string videoNameWithoutExt = Path.GetFileNameWithoutExtension(video.Path);
        string exactMatch = Path.Join(videoDir, videoNameWithoutExt + ".ass");
        if (File.Exists(exactMatch))
        {
            return exactMatch;
        }

        try
        {
            if (Directory.Exists(videoDir))
            {
                var assFiles = Directory.GetFiles(videoDir, videoNameWithoutExt + "*.ass")
                    .Where(f => !f.Contains("subsetted", StringComparison.OrdinalIgnoreCase))
                    .ToArray();

                if (assFiles.Length > 0)
                {
                    return assFiles[0];
                }
            }
        }
        catch
        {
            /* 忽略目录读取异常 */
        }

        return string.Empty;
    }
}
