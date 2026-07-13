using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace FFXIV_Craft_Architect.Tests;

public class CraftSessionStateTests
{
    [Fact]
    public void PlanStructureTransition_IncrementsPlanVersionMarksDirtyAndClearsProcurementOverlay()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        session.PublishProcurementOverlay(CreateOverlay(), "route generated");
        session.ClearDirtyBucket(CraftSessionDirtyBucket.Procurement);
        var before = session.CaptureVersionStamp();

        session.MarkPlanStructureChanged("project item added");

        Assert.Equal(before.PlanCore + 1, session.Versions.PlanCore);
        Assert.True(session.IsDirty(CraftSessionDirtyBucket.PlanCore));
        Assert.True(session.IsDirty(CraftSessionDirtyBucket.Procurement));
        Assert.False(session.HasProcurementOverlay);
        Assert.False(session.IsCurrent(before));
        Assert.Contains(session.Changes, change =>
            change.Scope.HasFlag(CraftSessionChangeScope.PlanCore)
            && change.InvalidatesProcurementOverlay);
    }

    [Fact]
    public void ActivatePlan_OwnsPlanProjectItemsAndActiveContext()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var plan = new CraftingPlan { Name = "Airship Parts", DataCenter = "Aether", World = "Jenova" };
        var projectItems = new[]
        {
            new ProjectItem { Id = 42, Name = "Hull Component", Quantity = 3, MustBeHq = true }
        };
        var context = new CraftSessionActiveContext("NA", "Aether", "Jenova", MarketFetchScope.SelectedDataCenter);

        session.ActivatePlan(plan, projectItems, context, "plan loaded");

        Assert.NotSame(plan, session.ActivePlan);
        Assert.Equal("Airship Parts", session.ActivePlan?.Name);
        Assert.Single(session.ProjectItems);
        Assert.Equal("Hull Component", session.ProjectItems[0].Name);
        Assert.Equal(context, session.ActiveContext);
        Assert.True(session.IsDirty(CraftSessionDirtyBucket.PlanCore));
    }

    [Fact]
    public void SessionOwnedSnapshots_DoNotExposeLivePlanProjectItemOrViewStateObjects()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var plan = new CraftingPlan { Name = "Original" };
        plan.SavedMarketPlans.Add(new DetailedShoppingPlan
        {
            ItemId = 10,
            Name = "Saved Market Plan",
            WorldOptions = { new WorldShoppingSummary { WorldName = "Jenova" } }
        });
        var item = new ProjectItem { Id = 7, Name = "Original Item", Quantity = 1 };
        session.ActivatePlan(plan, [item], new CraftSessionActiveContext(null, null, null, null), "plan loaded");

        session.ActivePlan!.Name = "Mutated Outside";
        session.ProjectItems[0].Name = "Mutated Outside";
        session.ViewState.ExpandedMarketWorlds.Add("Mutated Outside");
        plan.SavedMarketPlans[0].WorldOptions.Add(new WorldShoppingSummary { WorldName = "External Mutation" });
        session.ActivePlan!.SavedMarketPlans[0].WorldOptions.Add(new WorldShoppingSummary { WorldName = "Snapshot Mutation" });

        Assert.Equal("Original", session.ActivePlan?.Name);
        Assert.Equal("Original Item", session.ProjectItems[0].Name);
        Assert.Empty(session.ViewState.ExpandedMarketWorlds);
        Assert.Single(session.ActivePlan!.SavedMarketPlans[0].WorldOptions);
    }
    [Fact]
    public void PlanDecisionTransition_UsesPlanDirtyBucketAndInvalidatesProcurementOverlay()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        session.PublishProcurementOverlay(CreateOverlay(), "route generated");
        var before = session.CaptureVersionStamp();

        session.MarkPlanDecisionChanged("source changed");

        Assert.Equal(before.PlanDecision + 1, session.Versions.PlanDecision);
        Assert.True(session.IsDirty(CraftSessionDirtyBucket.PlanCore));
        Assert.False(session.HasProcurementOverlay);
    }

    [Fact]
    public void MarketAnalysisPublication_ReplacesEvidenceMarksMarketDirtyAndClearsProcurementOverlay()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        session.PublishProcurementOverlay(CreateOverlay(), "route generated");
        session.ClearDirtyBucket(CraftSessionDirtyBucket.Procurement);
        var before = session.CaptureVersionStamp();
        var analysis = new MarketItemAnalysis { ItemId = 9, Name = "Ingot", QuantityNeeded = 5 };

        session.PublishMarketAnalysis([analysis], [99], "analysis published");

        Assert.Equal(before.MarketAnalysis + 1, session.Versions.MarketAnalysis);
        Assert.True(session.IsDirty(CraftSessionDirtyBucket.MarketAnalysis));
        Assert.False(session.HasProcurementOverlay);
        Assert.Single(session.MarketEvidence.ItemAnalyses);
        Assert.Contains(99, session.MarketEvidence.UnavailableMarketItemIds);
    }

    [Fact]
    public void DirtyBuckets_AreTrackedIndependently()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());

        session.MarkPlanStructureChanged("project item added");
        session.PublishMarketAnalysis("analysis published");
        session.ClearDirtyBucket(CraftSessionDirtyBucket.PlanCore);

        Assert.False(session.IsDirty(CraftSessionDirtyBucket.PlanCore));
        Assert.True(session.IsDirty(CraftSessionDirtyBucket.MarketAnalysis));
        Assert.False(session.IsDirty(CraftSessionDirtyBucket.Procurement));
    }


    [Fact]
    public void ProcurementRouteSettingsChange_ClearsOverlayWithoutStalingMarketEvidence()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        session.PublishMarketAnalysis(
            [new MarketItemAnalysis { ItemId = 12, Name = "Ore", QuantityNeeded = 3 }],
            [],
            "analysis published");
        session.PublishProcurementOverlay(CreateOverlay(), "route generated");
        var before = session.CaptureVersionStamp();

        session.MarkProcurementRouteSettingsChanged("split world setting changed");

        Assert.Equal(before.MarketAnalysis, session.Versions.MarketAnalysis);
        Assert.Equal(before.SettingsContext, session.Versions.SettingsContext);
        Assert.Equal(before.Procurement + 1, session.Versions.Procurement);
        Assert.Single(session.MarketEvidence.ItemAnalyses);
        Assert.False(session.HasProcurementOverlay);
    }


    [Fact]
    public void TryPublishMarketAnalysis_RejectsStalePlanSnapshotFromSamePlanSession()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        session.ActivatePlan(
            CreatePlan(AcquisitionSource.Craft),
            [],
            new CraftSessionActiveContext(null, null, null, null),
            "plan loaded");
        var planSessionVersion = session.PlanSessionVersion;
        var stalePlan = session.ActivePlan!;
        var currentPlan = session.ActivePlan!;
        currentPlan.RootItems[0].Source = AcquisitionSource.VendorBuy;
        Assert.True(session.TryReplaceActivePlanDecisions(
            session.CaptureVersionStamp(),
            currentPlan,
            planSessionVersion,
            "newer decision"));

        var published = session.TryPublishMarketAnalysis(
            session.CaptureVersionStamp(),
            stalePlan,
            planSessionVersion,
            [new MarketItemAnalysis { ItemId = 1, Name = "Material", QuantityNeeded = 1 }],
            [],
            acquisitionDecisionsChanged: true,
            "stale market analysis");

        Assert.False(published);
        Assert.Equal(AcquisitionSource.VendorBuy, session.ActivePlan!.RootItems[0].Source);
        Assert.Empty(session.MarketEvidence.ItemAnalyses);
    }
    [Fact]
    public async Task TryPublishFrom_BlocksConcurrentSessionChangeUntilPublicationFinishes()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var stamp = session.CaptureVersionStamp();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var publishTask = Task.Run(() => session.TryPublishFrom(stamp, () =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            session.MarkViewStateChanged("published view change");
        }));

        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var competingChange = Task.Run(() => session.MarkPlanStructureChanged("racing plan change"));

        var earlyWinner = await Task.WhenAny(competingChange, Task.Delay(TimeSpan.FromMilliseconds(100)));
        Assert.NotSame(competingChange, earlyWinner);
        release.Set();
        Assert.True(await publishTask);
        await competingChange.WaitAsync(TimeSpan.FromSeconds(5));
    }


    [Fact]
    public void Dispatcher_IsUsedForSessionChangeNotifications()
    {
        var dispatcher = new RecordingCraftSessionDispatcher();
        var session = new CraftSessionState(dispatcher);
        var observed = false;
        session.Changed += (_, _) => observed = true;

        session.MarkViewStateChanged("selection changed");

        Assert.True(dispatcher.WasUsed);
        Assert.True(observed);
        Assert.True(session.IsDirty(CraftSessionDirtyBucket.ViewState));
    }


    private sealed class RecordingCraftSessionDispatcher : ICraftSessionDispatcher
    {
        public bool WasUsed { get; private set; }

        public void Dispatch(Action action)
        {
            WasUsed = true;
            action();
        }
    }

    private static CraftSessionProcurementOverlay CreateOverlay() =>
        new(DateTime.UtcNow, [1], "route generated");

    private static CraftingPlan CreatePlan(AcquisitionSource source) =>
        new()
        {
            RootItems =
            [
                new PlanNode
                {
                    ItemId = 1,
                    Name = "Material",
                    Quantity = 1,
                    Source = source,
                    CanCraft = true,
                    CanBuyFromVendor = true
                }
            ]
        };
}
