namespace FFXIV_Craft_Architect.ContractTests;

public sealed class UnifiedSettingsContractTests
{
    [Fact]
    public void LegacySettingsSurfacesAreStructurallyRemoved()
    {
        var web = Path.Combine(LocateRepositoryRoot(), "src", "FFXIV Craft Architect.Web");

        Assert.False(File.Exists(Path.Combine(web, "Dialogs", "OptionsDialog.razor")));
        Assert.False(File.Exists(Path.Combine(web, "Shared", "AccountDialog.razor")));
        Assert.False(File.Exists(Path.Combine(web, "Pages", "MemberWorkspaces.razor")));

        var source = string.Join("\n", Directory.EnumerateFiles(web, "*.razor", SearchOption.AllDirectories)
            .Select(File.ReadAllText));
        Assert.DoesNotContain("OptionsDialog", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AccountDialog", source, StringComparison.Ordinal);
        Assert.DoesNotContain("InitialPanelIndex", source, StringComparison.Ordinal);
        Assert.DoesNotContain("options-tab-list", source, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryEntryPointConvergesOnStableSettingsSections()
    {
        var web = Path.Combine(LocateRepositoryRoot(), "src", "FFXIV Craft Architect.Web");
        var layout = File.ReadAllText(Path.Combine(web, "Shared", "MainLayout.razor"));
        var account = File.ReadAllText(Path.Combine(web, "Shared", "AccountSignInControl.razor"));
        var switcher = File.ReadAllText(Path.Combine(web, "Shared", "TradeCompanySwitcher.razor"));
        var hub = File.ReadAllText(Path.Combine(web, "Pages", "CompanyHub.razor"));
        var market = File.ReadAllText(Path.Combine(web, "Pages", "MarketAnalysis.razor"));
        var procurement = File.ReadAllText(Path.Combine(web, "Pages", "ProcurementPlan.razor"));

        Assert.Contains("SettingsSection.MarketAndRoutes", layout, StringComparison.Ordinal);
        Assert.Contains("SettingsSection.Diagnostics", layout, StringComparison.Ordinal);
        Assert.Contains("SettingsSection.Profile", account, StringComparison.Ordinal);
        Assert.DoesNotContain("OnClick=\"SignInAsync\"", account, StringComparison.Ordinal);
        Assert.Contains("OnClick=\"OpenAccountAsync\"", account, StringComparison.Ordinal);
        Assert.Contains("SettingsSection.Workspace", switcher, StringComparison.Ordinal);
        Assert.Contains("SettingsSection.Workspace", hub, StringComparison.Ordinal);
        Assert.Contains("SettingsSection.MarketAndRoutes", market, StringComparison.Ordinal);
        Assert.Contains("SettingsSection.Integrations", procurement, StringComparison.Ordinal);
    }

    [Fact]
    public void WorkspaceSettingsDoNotDuplicateCompanyWorkAndPickerIsGlobal()
    {
        var web = Path.Combine(LocateRepositoryRoot(), "src", "FFXIV Craft Architect.Web");
        var workspace = File.ReadAllText(Path.Combine(web, "Shared", "WorkspaceSettingsPanel.razor"));
        var status = File.ReadAllText(Path.Combine(web, "Shared", "StatusBar.razor"));

        Assert.DoesNotContain("Assignments", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("Company updates", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("preferred character", workspace, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("transfer ownership", workspace, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<TradeCompanySwitcher", status, StringComparison.Ordinal);
        Assert.DoesNotContain("IsTradeRoute", status, StringComparison.Ordinal);
        Assert.Contains("test-readiness", File.ReadAllText(Path.Combine(
            web,
            "Services",
            "TradeCompany",
            "CompanyHubClient.cs")), StringComparison.Ordinal);
        Assert.DoesNotContain(">linked account<", workspace, StringComparison.Ordinal);
        Assert.Contains("Send test", workspace, StringComparison.Ordinal);
        Assert.Contains("CompanyChanged.InvokeAsync", workspace, StringComparison.Ordinal);
        var settings = File.ReadAllText(Path.Combine(web, "Dialogs", "SettingsDialog.razor"));
        Assert.Contains("OnWorkspaceCompanyChangedAsync", settings, StringComparison.Ordinal);
        Assert.Contains("hub.Standing.Role is \"owner\" or \"operator\"", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void SimpleSettingsPersistImmediatelyAndOnlyCompanyEditsUseGroupedSave()
    {
        var web = Path.Combine(LocateRepositoryRoot(), "src", "FFXIV Craft Architect.Web");
        var settings = File.ReadAllText(Path.Combine(web, "Dialogs", "SettingsDialog.razor"));

        Assert.Contains("OnSplitWorldPurchasesChanged", settings, StringComparison.Ordinal);
        Assert.Contains("OnMarketMafiosoEnabledChanged", settings, StringComparison.Ordinal);
        Assert.Contains("Settings.SetAsync", settings, StringComparison.Ordinal);
        Assert.Contains(
            "_activePanel == SettingsSection.CompanyAdministration",
            settings,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Save market settings", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Save integration settings", settings, StringComparison.Ordinal);
        Assert.Contains("ConfirmDiscardCompanyChangesAsync", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void LongTextDisclosureUsesSharedPopoverAndUnreadNeedsExplicitOpen()
    {
        var web = Path.Combine(LocateRepositoryRoot(), "src", "FFXIV Craft Architect.Web");
        var hub = File.ReadAllText(Path.Combine(web, "Pages", "CompanyHub.razor"));

        Assert.True(hub.Split("<FullTextPopover", StringSplitOptions.None).Length >= 3);
        Assert.DoesNotContain("note-popover", hub, StringComparison.Ordinal);
        Assert.Contains("SelectCommissionAsync", hub, StringComparison.Ordinal);
        Assert.Contains("MarkCommissionReadAsync", hub, StringComparison.Ordinal);
        Assert.DoesNotContain("@onmouseenter=\"Mark", hub, StringComparison.Ordinal);
    }

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "FFXIV Craft Architect.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ??
            throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
