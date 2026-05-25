using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Jellyfin.Plugin.AssSubsetter.Middleware;

/// <summary>
/// A startup filter to inject the middleware at the correct position in the pipeline.
/// </summary>
public class SubtitleInterceptorStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<SubtitleInterceptorMiddleware>();
            next(app);
        };
    }
}
