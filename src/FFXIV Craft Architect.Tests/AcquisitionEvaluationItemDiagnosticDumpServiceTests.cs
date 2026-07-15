using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.Web.Services;

namespace FFXIV_Craft_Architect.Tests;

public sealed class AcquisitionEvaluationItemDiagnosticDumpServiceTests
{
    [Fact]
    public void BuildDump_CapturesSelectedItemActionabilityEvidenceAndRefreshOutcome()
    {
        var appState = new AppState();
        var node = new PlanNode
        {
            NodeId = "material-200",
            ItemId = 200,
            Name = "Market Material",
            Quantity = 10,
            Source = AcquisitionSource.MarketBuyNq,
            SourceReason = AcquisitionSourceReason.SystemDefault,
            CanBuyFromMarket = true,
            CanBeHq = true,
            MarketPrice = 100,
            HqMarketPrice = 125
        };
        var plan = new CraftingPlan
        {
            Name = "Diagnostic Plan",
            DataCenter = "Aether",
            RootItems = [node]
        };
        var shoppingPlan = new DetailedShoppingPlan
        {
            ItemId = 200,
            Name = "Market Material",
            QuantityNeeded = 10,
            WorldOptions =
            [
                new WorldShoppingSummary
                {
                    DataCenter = "Aether",
                    WorldName = "Siren",
                    TotalCost = 1_000,
                    TotalQuantityPurchased = 10
                }
            ],
            RecommendedWorld = new WorldShoppingSummary
            {
                DataCenter = "Aether",
                WorldName = "Siren",
                TotalCost = 1_000,
                TotalQuantityPurchased = 10
            }
        };
        var analysis = new MarketItemAnalysis
        {
            ItemId = 200,
            Name = "Market Material",
            QuantityNeeded = 10
        };
        appState.ApplyBuiltRecipePlanWithActiveItems(plan);
        appState.TrackCurrentPlanIdentity("plan-200", "Diagnostic Plan");
        appState.ReplaceMarketAnalysis([analysis], [shoppingPlan]);
        var projection = new RecipeDemandProjectionService().Build(plan, snapshot: null);
        var row = Assert.Single(AcquisitionEvaluationSnapshotBuilder.Build(
            plan,
            [shoppingPlan],
            [],
            AcquisitionFilter.All,
            projection).Rows);
        var refresh = new MarketEvidenceHydrationRunSnapshot(
            MarketEvidenceHydrationStatus.Published,
            appState.PlanSessionVersion,
            "plan-200",
            "Diagnostic Plan",
            new DateTime(2026, 7, 15, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 15, 12, 0, 3, DateTimeKind.Utc),
            1,
            new MarketAnalysisWorkflowResult(true, 1, 0, 4),
            "Published actionable market evidence for 1 item(s).",
            null);
        var cacheDecision = new MarketCacheDecisionSnapshot
        {
            RequestedItemCount = 1,
            RequestedPairCount = 4,
            ForcedRefreshPairCount = 4,
            RefreshRequestedPairs = true
        };
        var service = new AcquisitionEvaluationItemDiagnosticDumpService();

        var dump = service.BuildDump(
            appState,
            row,
            shoppingPlan,
            refresh,
            cacheDecision,
            new DateTime(2026, 7, 15, 12, 1, 0, DateTimeKind.Utc));

        Assert.Equal("acquisition-evaluation-item-diagnostic-dump", dump.Tool);
        Assert.Equal(200, dump.Item.ItemId);
        Assert.Equal(10, dump.Item.TotalQuantity);
        Assert.True(dump.Actionability.Nq.IsDefaultEligible);
        Assert.Equal(1_000, dump.Actionability.Nq.Cost);
        Assert.Equal("Siren", dump.Actionability.Nq.WorldName);
        Assert.Single(dump.ShoppingPlans);
        Assert.Single(dump.MarketAnalyses);
        Assert.Equal(MarketEvidenceHydrationStatus.Published, dump.AutomaticRefresh.Status);
        Assert.Equal(4, dump.CacheDecision?.ForcedRefreshPairCount);
    }

    [Fact]
    public void Serialize_UsesReadableCamelCaseJsonAndStringEnums()
    {
        var appState = new AppState();
        var node = new PlanNode
        {
            NodeId = "material-200",
            ItemId = 200,
            Name = "Market Material",
            Quantity = 10,
            Source = AcquisitionSource.MarketBuyNq,
            CanBuyFromMarket = true
        };
        var plan = new CraftingPlan { RootItems = [node] };
        appState.ApplyBuiltRecipePlanWithActiveItems(plan);
        var projection = new RecipeDemandProjectionService().Build(plan, snapshot: null);
        var row = Assert.Single(AcquisitionEvaluationSnapshotBuilder.Build(
            plan,
            [],
            [],
            AcquisitionFilter.All,
            projection).Rows);
        var service = new AcquisitionEvaluationItemDiagnosticDumpService();
        var dump = service.BuildDump(
            appState,
            row,
            null,
            MarketEvidenceHydrationRunSnapshot.None,
            null,
            DateTime.UtcNow);

        var json = service.Serialize(dump);

        Assert.Contains(Environment.NewLine, json);
        Assert.Contains("\"tool\": \"acquisition-evaluation-item-diagnostic-dump\"", json);
        Assert.Contains("\"source\": \"MarketBuyNq\"", json);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(200, document.RootElement.GetProperty("item").GetProperty("itemId").GetInt32());
    }

    [Fact]
    public void CreateFileName_IncludesItemIdentityAndSanitizesInvalidCharacters()
    {
        var fileName = AcquisitionEvaluationItemDiagnosticDumpService.CreateFileName(
            "Bad/Item:Name",
            200,
            new DateTime(2026, 7, 15, 12, 34, 56, DateTimeKind.Utc));

        Assert.Equal("acquisition-200-Bad_Item_Name-20260715-123456.json", fileName);
    }
}
