using FFXIV_Craft_Architect.Core.Integrations.WorkshopHost;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Core.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace FFXIV_Craft_Architect.Tests;

public sealed class WorkshopHostCraftAppraisalServiceCollectionExtensionsTests
{
    [Fact]
    public void AddWorkshopHostCraftAppraisal_RegistersCorePlanBuilderAndAppraisalService()
    {
        var services = new ServiceCollection();

        services.AddWorkshopHostCraftAppraisal();

        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(RecipeCalculationService) &&
            descriptor.ImplementationType == typeof(RecipeCalculationService) &&
            descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IVendorCacheService) &&
            descriptor.ImplementationType == typeof(VendorCacheService) &&
            descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IRecipeResolutionService) &&
            descriptor.ImplementationType == typeof(RecipeResolutionService) &&
            descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(ICoreRecipePlanBuilder) &&
            descriptor.ImplementationType == typeof(CoreRecipeCalculationPlanBuilder) &&
            descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(ICraftAppraisalService) &&
            descriptor.ImplementationType == typeof(CraftAppraisalService) &&
            descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(ICraftAppraisalPriceEvidenceService) &&
            descriptor.ImplementationType == typeof(CraftAppraisalPriceEvidenceService) &&
            descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(IMarketCacheService) &&
            descriptor.Lifetime == ServiceLifetime.Scoped);
    }

    [Fact]
    public void AddWorkshopHostCraftAppraisal_ResolvesAppraisalServiceWithDefaultDependencies()
    {
        var services = new ServiceCollection();
        services.AddWorkshopHostCraftAppraisal();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        using var scope = provider.CreateScope();

        var appraisalService = scope.ServiceProvider.GetRequiredService<ICraftAppraisalService>();
        var evidenceService = scope.ServiceProvider.GetRequiredService<ICraftAppraisalPriceEvidenceService>();
        var marketCache = scope.ServiceProvider.GetRequiredService<IMarketCacheService>();

        Assert.IsType<CraftAppraisalService>(appraisalService);
        Assert.IsType<CraftAppraisalPriceEvidenceService>(evidenceService);
        Assert.IsAssignableFrom<IMarketCacheService>(marketCache);
    }

    [Fact]
    public void AddWorkshopHostCraftAppraisal_PreservesExistingCorePlanBuilder()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICoreRecipePlanBuilder, StubCoreRecipePlanBuilder>();

        services.AddWorkshopHostCraftAppraisal();

        var coreBuilderDescriptors = services
            .Where(descriptor => descriptor.ServiceType == typeof(ICoreRecipePlanBuilder))
            .ToList();
        var descriptor = Assert.Single(coreBuilderDescriptors);
        Assert.Equal(typeof(StubCoreRecipePlanBuilder), descriptor.ImplementationType);
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
    }

    private sealed class StubCoreRecipePlanBuilder : ICoreRecipePlanBuilder
    {
        public Task<CraftingPlan> BuildPlanAsync(
            List<(int itemId, string name, int quantity, bool isHqRequired)> targetItems,
            string dataCenter,
            string world,
            CancellationToken ct = default)
        {
            throw new NotSupportedException();
        }

        public Task FetchVendorPricesAsync(CraftingPlan plan, CancellationToken ct = default)
        {
            throw new NotSupportedException();
        }
    }
}
