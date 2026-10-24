using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Jellyfin.Plugin.AssSubsetter.Middleware;

/// <summary>
///     A startup filter to inject the middleware at the correct position in the pipeline.
/// </summary>
public class SubtitleInterceptorStartupFilter : IConfigureOptions<MvcOptions>
{
    /// <inheritdoc />
    public void Configure(MvcOptions options)
    {
        options.Filters.Add<SubtitleInterceptorResourceFilter>();
    }
}
