using Jellyfin.Plugin.AssSubsetter.Middleware;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Middleware;

public sealed class SubtitleInterceptorStartupFilterTests
{
    [Fact]
    public void Configure_RegistersResolvableMvcResourceFilterBeforeHostConfiguration()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddTransient<IConfigureOptions<MvcOptions>, SubtitleInterceptorStartupFilter>();
        using var provider = services.BuildServiceProvider();
        var filters = provider.GetRequiredService<IOptions<MvcOptions>>().Value.Filters;
        var factory = Assert.IsAssignableFrom<IFilterFactory>(Assert.Single(filters));
        Assert.IsType<SubtitleInterceptorResourceFilter>(factory.CreateInstance(provider));
    }
}
