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
    public void SessionOwnedSnapshots_DoNotExposeLiveVersionsMarketEvidenceOrProcurementOverlay()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var analysis = new MarketItemAnalysis
        {
            ItemId = 3,
            Name = "Ore",
            Worlds = { new WorldMarketAnalysis { WorldName = "Jenova" } }
        };
        session.PublishMarketAnalysis([analysis], [], "analysis published");
        session.PublishProcurementOverlay(
            new CraftSessionProcurementOverlay(
                DateTime.UtcNow,
                [3],
                "route",
                RouteCards: [new WorldProcurementCardModel { WorldName = "Siren", DataCenter = "Aether" }]),
            "route generated");

        var versionSnapshot = session.Versions;
        typeof(CraftSessionVersions)
            .GetProperty(nameof(CraftSessionVersions.MarketAnalysis))!
            .SetValue(versionSnapshot, 0);
        analysis.Worlds.Add(new WorldMarketAnalysis { WorldName = "External Mutation" });
        session.MarketEvidence.ItemAnalyses[0].Worlds.Add(new WorldMarketAnalysis { WorldName = "Snapshot Mutation" });
        ((int[])session.ProcurementOverlay!.ActiveItemIds)[0] = 99;
        session.ProcurementOverlay!.RouteCards![0].WorldName = "Mutated Outside";

        Assert.Equal(1, session.Versions.MarketAnalysis);
        Assert.Single(session.MarketEvidence.ItemAnalyses[0].Worlds);
        Assert.Equal(3, session.ProcurementOverlay!.ActiveItemIds[0]);
        Assert.Equal("Siren", session.ProcurementOverlay!.RouteCards![0].WorldName);
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
    public void ProcurementBucket_IsNotMarkedWhenInvalidationHasNoOverlayToClear()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());

        session.MarkPlanStructureChanged("project item added");

        Assert.True(session.IsDirty(CraftSessionDirtyBucket.PlanCore));
        Assert.False(session.IsDirty(CraftSessionDirtyBucket.Procurement));
    }

    [Fact]
    public void ProcurementOverlayPayload_IsOwnedAndClearedByInvalidatingTransition()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        var overlay = new CraftSessionProcurementOverlay(
            DateTime.UtcNow,
            [11, 12],
            "initial route");

        session.PublishProcurementOverlay(overlay, "route generated");

        Assert.True(session.HasProcurementOverlay);
        Assert.Equal(overlay.PublishedAtUtc, session.ProcurementOverlay?.PublishedAtUtc);
        Assert.Equal(overlay.SourceDescription, session.ProcurementOverlay?.SourceDescription);
        Assert.Equal(overlay.ActiveItemIds, session.ProcurementOverlay?.ActiveItemIds);

        session.MarkProcurementSettingsChanged("split world setting changed");

        Assert.False(session.HasProcurementOverlay);
        Assert.Null(session.ProcurementOverlay);
        Assert.True(session.IsDirty(CraftSessionDirtyBucket.Procurement));
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
    public void TemporaryProcurementExclusions_AreSessionOwnedAndClearOnlyProcurementOverlay()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        session.PublishMarketAnalysis(
            [new MarketItemAnalysis { ItemId = 12, Name = "Ore", QuantityNeeded = 3 }],
            [],
            "analysis published");
        session.PublishProcurementOverlay(CreateOverlay(), "route generated");
        var before = session.CaptureVersionStamp();
        var blacklistedWorld = new MarketWorldKey("Aether", "Siren");
        var excludedWorld = new MarketWorldKey("Aether", "Faerie");

        session.BlacklistMarketWorldTemporarily(
            blacklistedWorld,
            TimeSpan.FromMinutes(30),
            DateTimeOffset.Parse("2026-06-01T12:00:00Z"));
        session.ExcludeItemWorldTemporarily(55, excludedWorld);

        Assert.Equal(before.MarketAnalysis, session.Versions.MarketAnalysis);
        Assert.Equal(before.SettingsContext, session.Versions.SettingsContext);
        Assert.Contains(blacklistedWorld, session.GetActiveBlacklistedMarketWorlds(DateTimeOffset.Parse("2026-06-01T12:05:00Z")));
        Assert.Contains(new MarketItemWorldKey(55, excludedWorld), session.TemporarilyExcludedItemWorlds);
        Assert.Equal(2, session.GetActiveTemporaryExclusionCount(DateTimeOffset.Parse("2026-06-01T12:05:00Z")));
        Assert.Single(session.MarketEvidence.ItemAnalyses);
        Assert.False(session.HasProcurementOverlay);
    }

    [Fact]
    public void SettingsChange_ClearsMarketEvidenceAndProcurementOverlay()
    {
        var session = new CraftSessionState(new ImmediateCraftSessionDispatcher());
        session.PublishMarketAnalysis(
            [new MarketItemAnalysis { ItemId = 12, Name = "Ore", QuantityNeeded = 3 }],
            [],
            "analysis published");
        session.PublishProcurementOverlay(CreateOverlay(), "route generated");

        session.MarkProcurementSettingsChanged("market context changed");

        Assert.Empty(session.MarketEvidence.ItemAnalyses);
        Assert.Empty(session.MarketEvidence.ShoppingPlans!);
        Assert.False(session.HasProcurementOverlay);
        Assert.True(session.IsDirty(CraftSessionDirtyBucket.MarketAnalysis));
        Assert.True(session.IsDirty(CraftSessionDirtyBucket.Procurement));
        Assert.Contains(session.Changes, change =>
            change.Scope.HasFlag(CraftSessionChangeScope.SettingsContext) &&
            change.Scope.HasFlag(CraftSessionChangeScope.MarketAnalysis) &&
            change.Scope.HasFlag(CraftSessionChangeScope.ProcurementOverlay));
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

    [Theory]
    [InlineData("market.region", CraftSettingsKey.Region)]
    [InlineData("market.default_datacenter", CraftSettingsKey.DefaultDataCenter)]
    [InlineData("market.default_search_scope", CraftSettingsKey.DefaultMarketFetchScope)]
    [InlineData("market.auto_fetch_prices", CraftSettingsKey.AutoFetchMarketPrices)]
    [InlineData("market.home_world", CraftSettingsKey.HomeWorld)]
    [InlineData("market.exclude_congested_worlds", CraftSettingsKey.ExcludeCongestedWorlds)]
    [InlineData("market.include_cross_world", CraftSettingsKey.IncludeCrossWorld)]
    [InlineData("planning.default_recommendation_mode", CraftSettingsKey.RecommendationMode)]
    public void SettingsKeyMapping_CoversWebAndWpfSettingNames(string storageKey, CraftSettingsKey expected)
    {
        Assert.Equal(expected, CraftSettingsKeyMap.FromStorageKey(storageKey));
    }

    [Theory]
    [InlineData(CraftEvidenceOwner.PlanNodePrice, CraftSessionDirtyBucket.PlanCore)]
    [InlineData(CraftEvidenceOwner.MarketAnalysis, CraftSessionDirtyBucket.MarketAnalysis)]
    [InlineData(CraftEvidenceOwner.ProcurementOverlay, CraftSessionDirtyBucket.Procurement)]
    [InlineData(CraftEvidenceOwner.RawMarketCache, null)]
    public void EvidenceOwnership_MapsOnlySessionEvidenceToDirtyBuckets(CraftEvidenceOwner owner, CraftSessionDirtyBucket? expected)
    {
        Assert.Equal(expected, CraftEvidenceOwnership.GetDirtyBucket(owner));
    }

    [Fact]
    public void ServiceCollection_AllowsPlatformDispatcherOverride()
    {
        var provider = new ServiceCollection()
            .AddCraftSessionFoundation()
            .AddSingleton<RecordingCraftSessionDispatcher>()
            .AddSingleton<ICraftSessionDispatcher>(sp => sp.GetRequiredService<RecordingCraftSessionDispatcher>())
            .BuildServiceProvider();

        var session = provider.GetRequiredService<CraftSessionState>();
        session.MarkViewStateChanged("selection changed");

        Assert.True(provider.GetRequiredService<RecordingCraftSessionDispatcher>().WasUsed);
    }

    [Fact]
    public void WpfDispatcherAdapter_ImplementsCoreDispatcherContract()
    {
        Assert.Contains(
            typeof(ICraftSessionDispatcher),
            typeof(FFXIV_Craft_Architect.Services.WpfCraftSessionDispatcher).GetInterfaces());
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

    [Fact]
    public void CoreSessionTypes_DoNotReferenceWebOrWpfAssemblies()
    {
        var referencedAssemblies = typeof(CraftSessionState)
            .Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(referencedAssemblies, name => name.Contains(".Web", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(referencedAssemblies, name => name.Equals("PresentationFramework", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(referencedAssemblies, name => name.Equals("WindowsBase", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ServiceCollection_CanResolveSessionFoundation()
    {
        var provider = new ServiceCollection()
            .AddCraftSessionFoundation()
            .BuildServiceProvider();

        var first = provider.GetRequiredService<CraftSessionState>();
        var second = provider.GetRequiredService<CraftSessionState>();

        Assert.Same(first, second);
        Assert.Same(
            provider.GetRequiredService<ICraftSessionDispatcher>(),
            provider.GetRequiredService<ICraftSessionDispatcher>());
        Assert.NotNull(provider.GetRequiredService<ICraftOperationCoordinator>());
        Assert.NotNull(provider.GetRequiredService<CraftOperationState>());
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
