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
        Assert.Contains("Guild commission", source);
        Assert.Contains("Commission is @_commissionPercent.ToString(\"N0\")% of full estimated material cost", source);
        Assert.Contains("Provided materials", source);
        Assert.Contains("Commission on full estimate", source);
        Assert.DoesNotContain("MudNumericField", source);
        Assert.DoesNotContain("Handling", source);
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
