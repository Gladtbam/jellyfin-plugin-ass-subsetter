using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.AssSubsetter.Middleware;

/// <summary>
/// Runs subtitle replacement only after the host's authorization filters have completed.
/// </summary>
public sealed class SubtitleInterceptorResourceFilter : IAsyncResourceFilter
{
    /// <summary>Runs subtitle interception for the authorized request and uses the remaining MVC pipeline as its fallback.</summary>
    /// <param name="context">The authorized resource request and its request services.</param>
    /// <param name="next">The continuation for the remaining resource pipeline.</param>
    /// <returns>A task that completes when interception or the remaining pipeline finishes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> or <paramref name="next"/> is <see langword="null"/>.</exception>
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        RequestDelegate continuation = async _ => { await next().ConfigureAwait(false); };
        var interceptor = ActivatorUtilities.CreateInstance<SubtitleInterceptorMiddleware>(
            context.HttpContext.RequestServices, continuation);
        await interceptor.InvokeAsync(context.HttpContext).ConfigureAwait(false);
    }
}
