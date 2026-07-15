using Bunit;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Web.Services;
using FFXIV_Craft_Architect.Web.Shared;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace FFXIV_Craft_Architect.Tests;

[Trait(TestTraits.Surface, TestTraits.DeployWeb)]
public sealed class MarketMafiosoIntegrationStateTests
{
    [Fact]
    public void IntegrationIsDisabledByDefault()
    {
        var state = new MarketMafiosoIntegrationState();

        Assert.False(state.Enabled);
    }

    [Fact]
    public void SetEnabledNotifiesOnlyWhenValueChanges()
    {
        var state = new MarketMafiosoIntegrationState();
        var changes = 0;
        state.Changed += () => changes++;

        state.SetEnabled(true);
        state.SetEnabled(true);
        state.SetEnabled(false);

        Assert.False(state.Enabled);
        Assert.Equal(2, changes);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void ProcurementHandoffActionRequiresExplicitOptIn(bool enabled, bool shouldRenderAction)
    {
        using var context = new BunitContext();
        context.Services.AddMudServices();
        context.Services.AddSingleton(new AppState());
        context.JSInterop.Mode = JSRuntimeMode.Loose;

        var world = new WorldShoppingSummary
        {
            DataCenter = "Aether",
            WorldName = "Siren",
            AveragePricePerUnit = 100,
            TotalCost = 1_000,
            TotalQuantityPurchased = 10,
            HasSufficientStock = true,
        };
        var plan = new DetailedShoppingPlan
        {
            ItemId = 1,
            Name = "Darksteel Ore",
            QuantityNeeded = 10,
            RecommendedWorld = world,
            WorldOptions = [world],
        };

        var component = context.Render<ProcurementRouteTreePanel>(parameters => parameters
            .Add(panel => panel.ShoppingPlans, [plan])
            .Add(panel => panel.MarketMafiosoEnabled, enabled));

        Assert.Equal(
            shouldRenderAction,
            component.FindAll("button").Any(button => button.TextContent.Contains("Send to MM", StringComparison.Ordinal)));
    }
}
