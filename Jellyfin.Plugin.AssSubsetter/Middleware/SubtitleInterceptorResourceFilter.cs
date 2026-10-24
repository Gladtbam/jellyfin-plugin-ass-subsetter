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
    /// <inheritdoc />
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        RequestDelegate continuation = async _ => { await next().ConfigureAwait(false); };
        var interceptor = ActivatorUtilities.CreateInstance<SubtitleInterceptorMiddleware>(
            context.HttpContext.RequestServices, continuation);
        await interceptor.InvokeAsync(context.HttpContext).ConfigureAwait(false);
    }
}
