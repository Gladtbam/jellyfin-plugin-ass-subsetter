using System;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Jellyfin.Plugin.AssSubsetter.Middleware;

/// <summary>
///     A startup filter to inject the middleware at the correct position in the pipeline.
/// </summary>
public class SubtitleInterceptorStartupFilter : IConfigureOptions<MvcOptions>
{
    /// <summary>Registers the subtitle interceptor as an MVC resource filter so it runs after authorization.</summary>
    /// <param name="options">The MVC options that receive the subtitle filter.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    public void Configure(MvcOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.Filters.Add<SubtitleInterceptorResourceFilter>();
    }
}
