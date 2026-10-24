using Jellyfin.Plugin.AssSubsetter.Configuration;
using Jellyfin.Plugin.AssSubsetter.Fonts.Analysis;
using Jellyfin.Plugin.AssSubsetter.Fonts.Api;
using Jellyfin.Plugin.AssSubsetter.Fonts.Inventory;
using Jellyfin.Plugin.AssSubsetter.Fonts.Maintenance;
using Jellyfin.Plugin.AssSubsetter.Fonts.Persistence;
using Jellyfin.Plugin.AssSubsetter.ScheduledTasks;
using MediaBrowser.Controller;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace Jellyfin.Plugin.AssSubsetter.Tests.Fonts.Persistence;

public sealed class FontRepositoryCapabilityRegistrationTests
{
    [Fact]
    public void CapabilityAliases_ResolveTheAggregateSingletonAcrossScopes()
    {
        var repository = new Mock<IFontRepository>(MockBehavior.Strict);
        ServiceCollection services = CreateServices(repository.Object);
        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using IServiceScope scope = provider.CreateScope();

        // GetInterfaces includes transitive capabilities, so new facets also require a production alias.
        Type[] capabilities = typeof(IFontRepository).GetInterfaces();
        Assert.NotEmpty(capabilities);
        Assert.Same(repository.Object, provider.GetRequiredService<IFontRepository>());
        foreach (Type capability in capabilities)
        {
            ServiceDescriptor registration = Assert.Single(services, item => item.ServiceType == capability);
            Assert.Equal(ServiceLifetime.Singleton, registration.Lifetime);
            Assert.Same(repository.Object, provider.GetRequiredService(capability));
            Assert.Same(repository.Object, scope.ServiceProvider.GetRequiredService(capability));
        }

        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public void MigratedConsumers_ResolveWithOnlyOneAggregateRepositoryMock()
    {
        var repository = new Mock<IFontRepository>(MockBehavior.Strict);
        ServiceCollection services = CreateServices(repository.Object);
        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        Assert.NotNull(provider.GetRequiredService<FontManagementQuery>());
        Assert.IsType<PhysicalFontAnalysisEvidenceSource>(provider.GetRequiredService<IFontAnalysisEvidenceSource>());
        Assert.NotNull(provider.GetRequiredService<FontOperationRequestStore>());
        Assert.NotNull(provider.GetRequiredService<FontOperationRequestScheduler>());
        Assert.NotNull(provider.GetRequiredService<FontRenameCandidateRefreshQueue>());
        Assert.IsType<FontMaintenanceTaskExecutor>(provider.GetRequiredService<IFontMaintenanceTaskExecutor>());

        // These entry points are activated outside ordinary singleton resolution in production.
        Assert.NotNull(ActivatorUtilities.CreateInstance<FontManagementController>(provider));
        Assert.NotNull(ActivatorUtilities.CreateInstance<FontRenameStateQuery>(provider));
        Assert.NotNull(ActivatorUtilities.CreateInstance<FontRenameRecoveryService>(provider));

        // Resolution must neither initialize storage nor execute maintenance work.
        repository.VerifyNoOtherCalls();
    }

    private static ServiceCollection CreateServices(IFontRepository repository)
    {
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, Mock.Of<IServerApplicationHost>());
        services.RemoveAll<IFontRepository>();
        services.AddSingleton(repository);
        services.AddSingleton(FontStoragePaths.Create(Path.GetTempPath()));
        services.AddSingleton<Func<PluginConfiguration>>(() => new());
        services.AddSingleton(Mock.Of<ITaskManager>());
        services.AddLogging();
        return services;
    }
}
