using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.AssSubsetter.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.AssSubsetter.Fonts.Api;

/// <summary>Waits for startup recovery before accessing the font catalog.</summary>
/// <param name="readiness">The shared startup recovery result.</param>
/// <param name="lifetime">Cancels pending requests when the host stops.</param>
public sealed class FontCatalogReadinessFilter(FontCatalogReadiness readiness, IHostApplicationLifetime lifetime) : IAsyncActionFilter
{
    /// <summary>Runs naming-rule validation immediately and waits for catalog recovery before running database-backed actions.</summary>
    /// <param name="context">Provides action metadata, request cancellation, and the response slot.</param>
    /// <param name="next">Executes the action and the remaining action filters.</param>
    /// <returns>A task that completes after the action pipeline runs or the filter sets a 503 response because recovery failed.</returns>
    /// <remarks>The request abort token and the host stopping token both cancel the wait.</remarks>
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        // The settings page validates naming rules even when the catalog is unavailable.
        if (context.ActionDescriptor is ControllerActionDescriptor { MethodInfo.Name: nameof(FontManagementController.ValidateNamingRules) })
        {
            await next().ConfigureAwait(false);
            return;
        }

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.HttpContext.RequestAborted, lifetime.ApplicationStopping);
        if (!await readiness.WaitAsync(cancellation.Token).ConfigureAwait(false))
        {
            context.Result = new StatusCodeResult(StatusCodes.Status503ServiceUnavailable);
            return;
        }

        await next().ConfigureAwait(false);
    }
}
