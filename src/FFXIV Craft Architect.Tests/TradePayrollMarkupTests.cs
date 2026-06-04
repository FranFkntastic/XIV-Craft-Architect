namespace FFXIV_Craft_Architect.Tests;

public class TradePayrollMarkupTests
{
    [Fact]
    public void TradePayrollPage_IsDeveloperModeGuardedAndPayrollFocused()
    {
        var source = File.ReadAllText(GetTradePayrollPath());

        Assert.Contains("@page \"/trade\"", source);
        Assert.Contains("AppState.SecretDebugToolsEnabled", source);
        Assert.Contains("NavigationManager.NavigateTo(\"./\")", source);
        Assert.Contains("Payroll Calculator", source);
        Assert.Contains("New payroll calculation from active craft plan", source);
        Assert.Contains("Copy Payroll Summary", source);
        Assert.Contains("title=\"@line.UnitCostExplanation\"", source);
        Assert.Contains("Responsibility", source);
        Assert.Contains("CommissionMaterialResponsibility.Crafter", source);
        Assert.Contains("CommissionMaterialResponsibility.Provided", source);
        Assert.Contains("Commissioner name", source);
        Assert.Contains("FormatPayrollDate(_source.ImportedAtUtc)", source);
        Assert.Contains("Being made", source);
        Assert.Contains("_source.CraftedItems", source);
        Assert.Contains("Guild commission", source);
        Assert.Contains("Commission is @_commissionPercent.ToString(\"N0\")% of full estimated material cost", source);
        Assert.Contains("Crafter procures", source);
        Assert.Contains("Provided by commissioner", source);
        Assert.DoesNotContain("MudNumericField", source);
        Assert.DoesNotContain("Handling", source);
    }

    [Fact]
    public void TradePayrollSummary_UsesCommissionerAndMaterialResponsibilitySections()
    {
        var source = File.ReadAllText(GetTradePayrollPath());

        Assert.Contains("summary.AppendLine($\"Plan: {FormatPayrollDate(_source!.ImportedAtUtc)}\")", source);
        Assert.Contains("summary.AppendLine($\"Name: {FormatCommissionerName()}\")", source);
        Assert.Contains("AppendCraftedItemsSection(summary, _source.CraftedItems)", source);
        Assert.Contains("AppendMaterialSection(summary, \"Crafter procures\"", source);
        Assert.Contains("AppendMaterialSection(summary, \"Provided by commissioner\"", source);
        Assert.Contains("Materials reimbursement", source);
        Assert.DoesNotContain("summary.AppendLine($\"Plan: {_source!.SourcePlanName}\")", source);
        Assert.DoesNotContain("summary.AppendLine($\"Cost basis:", source);
        Assert.DoesNotContain("summary.AppendLine($\"Evidence:", source);
        Assert.DoesNotContain("Provided materials excluded", source);
        Assert.DoesNotContain("Commission on full estimate", source);
    }

    [Fact]
    public void TradePayrollPage_DoesNotMentionCustomerQuotes()
    {
        var source = File.ReadAllText(GetTradePayrollPath());

        Assert.DoesNotContain("Quote", source);
        Assert.DoesNotContain("Customer", source);
    }

    private static string GetTradePayrollPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "FFXIV Craft Architect.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "src", "FFXIV Craft Architect.Web", "Pages", "TradePayroll.razor");
    }
}
