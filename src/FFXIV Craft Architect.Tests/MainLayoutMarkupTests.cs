using System.Text.RegularExpressions;

namespace FFXIV_Craft_Architect.Tests;

public class MainLayoutMarkupTests
{
    [Fact]
    public void MainLayout_DeveloperMenu_UsesClickableExpandableToolsSection()
    {
        var source = File.ReadAllText(GetMainLayoutPath());
        var debugActivator = Regex.Match(
            source,
            "<MudMenuItem\\s+OnClick=\"ToggleDebugToolsMenu\"[\\s\\S]*?</MudMenuItem>",
            RegexOptions.CultureInvariant);

        Assert.True(debugActivator.Success);
        Assert.Contains("Icon=\"@Icons.Material.Filled.BugReport\"", debugActivator.Value);
        Assert.Contains("AutoClose=\"false\"", debugActivator.Value);
        Assert.Contains("<span>Developer</span>", debugActivator.Value);
        Assert.Contains("_debugToolsMenuOpen", source);
        Assert.Contains("Dump Selected Market Analysis Item", source);
        Assert.DoesNotContain("ActivationEvent=\"MouseEvent.MouseOver\"", debugActivator.Value);
        Assert.DoesNotContain("Label=\"Debug\"", source);
        Assert.DoesNotContain("<ActivatorContent>", debugActivator.Value);
    }

    [Fact]
    public void MainLayout_DebugMenu_IsAvailableForDevelopmentOrSecretToggle()
    {
        var source = File.ReadAllText(GetMainLayoutPath());

        Assert.Contains("@if (IsDebugToolsAvailable)", source);
        Assert.Contains(
            "IsDevelopment || AppState.SecretDebugToolsEnabled",
            source);
        Assert.Contains("if (!IsDebugToolsAvailable)", source);
    }

    [Fact]
    public void MainLayout_TradeWorkspaceSwitcher_IsAvailableOutsideDeveloperMode()
    {
        var source = File.ReadAllText(GetMainLayoutPath());

        Assert.DoesNotContain("<MudText Style=\"color: #d4a73a; font-weight: 600; margin-right: 24px;\">FFXIV Craft Architect</MudText>", source);
        Assert.Contains("FFXIV Trade Architect", source);
        Assert.Contains("NavigateTo(\"trade/orders\")", source);
        Assert.Contains("IsTradeMode", source);
        Assert.Contains("font-size: 1rem", source);
        Assert.Contains("margin-right: 24px", source);
        Assert.DoesNotContain(">Payroll", source);
        Assert.DoesNotContain("Experimental", source);
        Assert.DoesNotContain("Color.Warning", source);
    }

    [Fact]
    public void MainLayout_TradeRoute_RemainsActiveWhenDeveloperModeTurnsOff()
    {
        var source = File.ReadAllText(GetMainLayoutPath());

        Assert.DoesNotContain("!AppState.SecretDebugToolsEnabled && IsTradeMode", source);
        Assert.Contains("relativePath.StartsWith(\"trade\"", source);
    }

    [Fact]
    public void MainLayout_BenchmarkHooks_AreDeveloperModeGated()
    {
        var source = File.ReadAllText(GetMainLayoutPath());

        Assert.Contains("BenchmarkHook(", source);
        Assert.Contains("AppState.SecretDebugToolsEnabled ? id : null", source);
        Assert.Contains("data-benchmark-id=\"@BenchmarkHook(\"", source);
        Assert.Contains("main-import-menu", source);
        Assert.Contains("main-import-native-plan", source);
        Assert.Contains("main-nav-market-analysis", source);
    }

    [Fact]
    public void MainLayout_ReRendersWhenSettingsChange()
    {
        var source = File.ReadAllText(GetMainLayoutPath());

        Assert.Contains("AppState.OnStateChanged += OnAppStateChanged", source);
        Assert.Contains("AppState.OnStateChanged -= OnAppStateChanged", source);
        Assert.Contains("change.HasScope(AppStateChangeScope.Settings)", source);
    }

    [Fact]
    public void MarketSearchScope_DefaultsToEntireRegionWhenSettingIsMissing()
    {
        var mainLayout = File.ReadAllText(GetMainLayoutPath());
        var webSettings = File.ReadAllText(GetWebSettingsServicePath());

        Assert.Contains(
            "Settings.GetAsync(\"market.default_search_scope\", nameof(MarketFetchScope.EntireRegion))",
            mainLayout);
        Assert.Contains(
            ": MarketFetchScope.EntireRegion",
            mainLayout);
        Assert.Contains(
            "[\"market.default_search_scope\"] = \"EntireRegion\"",
            webSettings);
    }

    [Fact]
    public void MainLayout_AppBar_DoesNotExposeArchivedAboutPage()
    {
        var source = File.ReadAllText(GetMainLayoutPath());

        Assert.DoesNotContain("Href=\"about\"", source);
        Assert.DoesNotContain(">About</MudLink>", source);
        Assert.Contains("Href=\"https://github.com/FranFkntastic/XIV-Craft-Architect\"", source);
    }

    [Fact]
    public void MainLayout_QuickCopy_PrefersChoiceAwarePlanMaterialsBeforeShoppingFallback()
    {
        var source = File.ReadAllText(GetMainLayoutPath());

        Assert.Contains("@inject IRecipeLayerWorkflowService RecipeLayerWorkflowService", source);
        Assert.Contains("var materials = CreateMaterialsForQuickCopy();", source);
        Assert.Contains("BuildActiveProcurementItems(AppState.CurrentPlan)", source);
        Assert.Contains("? planMaterials", source);
        Assert.Contains(": CreateMaterialsFromShoppingItems()", source);
        Assert.Contains("UnitPrice = item.UnitPrice", source);
    }

    private static string GetMainLayoutPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "FFXIV Craft Architect.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "src", "FFXIV Craft Architect.Web", "Shared", "MainLayout.razor");
    }

    private static string GetWebSettingsServicePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "FFXIV Craft Architect.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "src", "FFXIV Craft Architect.Web", "Services", "WebSettingsService.cs");
    }
}
