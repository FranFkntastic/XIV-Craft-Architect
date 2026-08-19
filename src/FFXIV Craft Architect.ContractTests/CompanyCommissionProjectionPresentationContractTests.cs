using System.Reflection;
using System.Text.Json;
using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.LodestoneLookup.Services.Discord;
using FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;
using FFXIV_Craft_Architect.Web.Pages;
using FFXIV_Craft_Architect.Web.Services;
using FFXIV_Craft_Architect.Web.Services.TradeCompany;
namespace FFXIV_Craft_Architect.ContractTests;

public sealed class CompanyCommissionProjectionPresentationContractTests
{
    private static readonly DateTime CapturedAt = new(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void LiveSamePlanFailureReplacesGenericDraftWarningWithoutChangingPayment()
    {
        const string exactFailure =
            "No complete executable route fits company policy and current listing evidence (8 worlds, 3 data-center transfers, 15% consolidation premium, listings at most 120 minutes old).";
        var breakdown = new TradePaymentContractBreakdown(
            TradePaymentContractMode.LaborStandard,
            IsAvailable: false,
            MaterialReimbursementTotal: 0,
            CommissionPercent: 0,
            CommissionAmount: 0,
            CraftLaborTotal: 0,
            CraftSynthCount: 0,
            GilPerSynth: 0,
            Total: 0,
            CraftLaborLines: [],
            Warnings: []);
        var summary = new TradeCommissionPaymentSummary(
            Materials: [],
            EstimatedProcurementTotal: 7_477_139,
            MaterialReimbursementTotal: 0,
            ProvidedMaterialTotal: 0,
            CommissionPercent: 0,
            CommissionAmount: 0,
            TotalPayment: 0,
            Warnings:
            [
                "Copper Ore uses very old market data from Goblin uploaded 22h ago.",
                "Executable material quote is unavailable."
            ],
            Legacy: breakdown,
            LaborStandard: breakdown,
            Active: breakdown);
        var live = new WorkerTradeProjection(
            Revision: 7,
            HasPlan: true,
            PlanId: "j11-gold-ore",
            PlanName: "J11 Gold Ore route quote proof 2026-08-18",
            SelectedDataCenter: "Aether",
            SelectedRegion: "North America",
            MarketFetchScope: MarketFetchScope.EntireRegion,
            RequestedDataCenters: ["Aether", "Crystal", "Dynamis", "Primal"],
            MarketLens: MarketAcquisitionLens.MinimumUpfrontCost,
            PlanSessionVersion: 4,
            MarketAnalysisVersion: 9,
            CraftedItems: [],
            RootItems: [],
            MaterialLines:
            [
                new CommissionPayrollInputLine(
                    5111,
                    "Copper Ore",
                    4_995,
                    2,
                    false,
                    CommissionMaterialResponsibility.Crafter,
                    "Vendor price",
                    "the selected vendor price",
                    EvidenceTimestampUtc: null,
                    Warnings: [])
            ],
            ActiveProcurementItems: [],
            AcquisitionRows: [],
            CraftLabor: [],
            Warnings: [exactFailure],
            MaterialQuote: null,
            MaterialQuoteFailureReason: null);

        var reconciled = (TradeCommissionPaymentSummary)typeof(TradeOrders)
            .GetMethod(
                "ReconcileVisibleQuoteWarnings",
                BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [summary, live, true, "Prior route failure."])!;

        Assert.Equal(summary with { Warnings = reconciled.Warnings }, reconciled);
        Assert.Equal([exactFailure], reconciled.Warnings);

        var savedQuoteWarnings = (TradeCommissionPaymentSummary)typeof(TradeOrders)
            .GetMethod(
                "ReconcileVisibleQuoteWarnings",
                BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [summary, live, false, null])!;
        Assert.Equal(summary with { Warnings = savedQuoteWarnings.Warnings }, savedQuoteWarnings);
        Assert.Equal(["Executable material quote is unavailable."], savedQuoteWarnings.Warnings);

        var mixedQualitySources = live with
        {
            MaterialLines =
            [
                .. live.MaterialLines,
                new CommissionPayrollInputLine(
                    5111,
                    "Copper Ore",
                    1,
                    9,
                    true,
                    CommissionMaterialResponsibility.Crafter,
                    "Market coverage",
                    "current HQ listings",
                    CapturedAt,
                    [])
            ]
        };
        var qualityAwareWarnings = (TradeCommissionPaymentSummary)typeof(TradeOrders)
            .GetMethod(
                "ReconcileVisibleQuoteWarnings",
                BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [summary, mixedQualitySources, false, null])!;
        Assert.Equal(summary.Warnings, qualityAwareWarnings.Warnings);
    }

    [Fact]
    public void DiscordPublicationUsesCurrentCraftingAndDeliveryLanguage()
    {
        AssertPublication(
            TradeOrderStatus.Assigned,
            ["CRAFTING", "3 completed"],
            "READY TO WORK",
            "IN PROGRESS",
            "ready");
        AssertPublication(
            TradeOrderStatus.AwaitingDelivery,
            ["AWAITING DELIVERY REVIEW"],
            "READY FOR DELIVERY");
    }
    [Fact]
    public void OrderCenterAndDraftMigrationKeepAuthorityBoundariesExplicit()
    {
        var repositoryRoot = LocateRepositoryRoot();
        Assert.Equal(2, ReadWebSource(repositoryRoot, "Services", "TradeOrderDraftFactory.cs").Split("AuthoringSchemaVersion = TradeOrder.CurrentAuthoringSchemaVersion").Length - 1);
        var (legacy, modern) = (JsonSerializer.Deserialize<TradeOrder>("{}")!, new TradeOrder { AuthoringSchemaVersion = TradeOrder.CurrentAuthoringSchemaVersion });
        Assert.Equal(0, legacy.AuthoringSchemaVersion);
        Assert.True(CompanyCommissionSchemaMigrationHostedService.RequiresMigration(legacy));
        Assert.False(CompanyCommissionSchemaMigrationHostedService.RequiresMigration(modern));
        Assert.False(CompanyCommissionSchemaMigrationHostedService.RequiresMigration(new TradeOrder { AuthoringSchemaVersion = 2 }));
        Assert.Equal(modern.AuthoringSchemaVersion, TradeOrderWorkflow.CopyOrder(modern).AuthoringSchemaVersion);
        modern.Status = TradeOrderStatus.Assigned;
        modern.AssignedCrafterId = Guid.NewGuid();
        modern.CompanyCommission = CreateClearedAssignedCommission() with { ActiveClaim = null };
        Assert.True(CompanyCommissionSchemaMigrationHostedService.RequiresMigration(modern));
        modern.CompanyCommission = modern.CompanyCommission with
        {
            SchemaVersion = TradeCompanyCommission.CurrentSchemaVersion - 1,
            ActiveClaim = new CompanyCommissionClaim(Guid.NewGuid(), 1, CapturedAt, modern.AssignedCrafterId, null)
        };
        Assert.True(CompanyCommissionSchemaMigrationHostedService.RequiresMigration(modern));
        var source = ReadWebSource(repositoryRoot, "Pages", "TradeOrders.razor");
        var pageStyles = ReadWebSource(repositoryRoot, "Pages", "TradeOrders.razor.css").ReplaceLineEndings("\n");
        var overviewSource = ReadWebSource(repositoryRoot, "Shared", "TradeOrderCenterOverview.razor");
        var overviewStyles = ReadWebSource(repositoryRoot, "Shared", "TradeOrderCenterOverview.razor.css");
        var detailsStart = source.IndexOf("<details class=\"trade-orders-work-details\"", StringComparison.Ordinal);
        var workspaceStart = source.LastIndexOf("<main class=\"trade-orders-workspace\">", detailsStart, StringComparison.Ordinal);
        var selectedOrderStart = source.LastIndexOf("else if (BuildSelectedOrderCenterOverview() is { } centerOverview)", detailsStart, StringComparison.Ordinal);
        var cardStart = source.IndexOf("<section class=\"trade-orders-workspace-card\">", selectedOrderStart, StringComparison.Ordinal);
        var detailsEnd = source.IndexOf("</details>", detailsStart, StringComparison.Ordinal);
        Assert.True(workspaceStart >= 0 && selectedOrderStart > workspaceStart && cardStart > selectedOrderStart && detailsStart > cardStart && detailsEnd > detailsStart);
        var centerBeforeDetails = source[..detailsStart];
        var calculationDetails = source[detailsStart..detailsEnd];
        Contains(centerBeforeDetails, "Crafter confirmed payment receipt", "crafterReceipt.TermsVersion",
            "OnClick=\"DeleteSelectedOrderAsync\"");
        Omits(centerBeforeDetails, "crafterReceipt.TermsVersion == operationsCommission.CurrentTermsVersion");
        Omits(calculationDetails, "@bind-Value", "ValueChanged=", "OpenSupplyPlanAsync",
            "DeleteSelectedOrderAsync", "OpenCloseOrderDialogAsync");
        Omits(source, "Edit Supply Plan");
        Contains(source, "Text=\"Plan\"", "ChangeProcurementRowResponsibilityValueAsync",
            "OnClick=\"InvokeSelectedOrderLifecycleActionAsync\"", "@SelectedLifecycleActionLabel",
            "class=\"trade-orders-timeline-visibility\" role=\"group\" aria-label=\"Choose timeline entry visibility\"",
            "aria-pressed=\"@(_timelineComposerVisibility == CommissionTimelineVisibility.CompanyOnly)\"",
            "aria-pressed=\"@(_timelineComposerVisibility == CommissionTimelineVisibility.Shared)\"",
            "operationsCommission.ActiveClaim?.AccountEvidence",
            "OAuth verified · User ID");
        var overviewStart = source.IndexOf("<TradeOrderCenterOverview", selectedOrderStart, StringComparison.Ordinal); var headerActionsStart = source.IndexOf("<HeaderActions>", overviewStart, StringComparison.Ordinal);
        var lifecycleActionStart = source.IndexOf("OnClick=\"InvokeSelectedOrderLifecycleActionAsync\"", headerActionsStart, StringComparison.Ordinal); var requirementsStart = source.IndexOf("<RequirementsContent>", headerActionsStart, StringComparison.Ordinal);
        Assert.True(overviewStart >= 0 && headerActionsStart > overviewStart && lifecycleActionStart > headerActionsStart && requirementsStart > lifecycleActionStart);
        Contains(overviewSource, "trade-order-overview__actions", "@HeaderActions", "public RenderFragment? HeaderActions");
        Assert.Equal(1, source.Split("\"Refresh Prices\"", StringSplitOptions.None).Length - 1);
        Omits(source, "Reprice Order");
        var procurementSource = ReadWebSource(repositoryRoot, "Pages", "TradeOrders.Procurement.cs");
        Contains(procurementSource, "IsRequestedOutputRow(row)", "trade-orders-output-chip", "Output", "GetVisibleLiveProcurementSnapshot()");
        Contains(procurementSource,
            "var commission = SelectedCanonicalCommission ?? _selectedOrder.CompanyCommission;",
            "return commission != null",
            "CreateCanonicalTermsWorkPackage(_selectedOrder, commission.CurrentTerms)",
            "ReconcileVisibleQuoteWarnings(",
            "live.MaterialQuoteFailureReason",
            "Executable material quote is unavailable.",
            "IsIrrelevantMarketWarningForSelectedSource(");
        Contains(source,
            "SelectedCanonicalCommission is { } canonicalCommission",
            "canonicalCommission.CurrentTerms.PricingEvidence.MaterialQuote",
            ": _selectedOrder.SourceSnapshot.MaterialQuote;");
        Omits(source,
            "SelectedCanonicalCommission?.CurrentTerms.PricingEvidence.MaterialQuote ??");
        var companyCommissionSource = ReadWebSource(repositoryRoot, "Pages", "TradeOrders.CompanyCommission.cs");
        Contains(companyCommissionSource,
            "copy.SourceSnapshot.Warnings = terms.PricingEvidence.Warnings?.ToArray() ?? [];",
            "terms.PricingEvidence.MaterialQuoteFailureReason;",
            "SelectedCanonicalCommission != null ||",
            "_selectedOrder?.CompanyCommission != null;");
        Omits(procurementSource,
            "GetCurrentLiveProcurementSnapshot()?.Warnings",
            "MaterialReimbursementTotal =");
        var craftPlanSource = ReadWebSource(repositoryRoot, "Pages", "TradeOrders.CraftPlan.cs");
        Contains(craftPlanSource,
            "pricingResult.UpdatedOrder.SourceSnapshot.MaterialQuoteFailureReason",
            "Order pricing could not be saved.",
            "var saved = !HasCanonicalCommission");
        Omits(craftPlanSource,
            "_selectedOrder.CompanyCommission == null",
            "_selectedOrder.CompanyCommission != null");
        Contains(ReadWebSource(repositoryRoot, "Services", "EngineWorkerSessionHost.cs"),
            "new CommissionCostBasisResolver().BuildSelectedSourceLines(",
            "materialLines = ApplyOnHandReferenceValues(quoteResult.MaterialLines, acquisition.Rows);");
        Omits(procurementSource, "IsRequestedOutputReferenceRow");
        Contains(ReadWebSource(repositoryRoot, "Services", "TradeProcurementRowBuilder.cs"), "output.MustBeHq == row.RequiresHq");
        Contains(ReadWebSource(repositoryRoot, "Pages", "TradeOrders.Selection.cs"), "Rebuild from Requested Outputs", "isSameLinkedPlan", "if (!isSameOrder)");
        Contains(ReadWebSource(repositoryRoot, "Pages", "TradeOrders.Selection.cs"),
            "if (query.Length < 2)",
            "SearchDeepArchivedOrdersAsync",
            "GroupBy(record => record.OrderId)");
        var lifecycleSource = ReadWebSource(repositoryRoot, "Services", "TradeCompany", "TradeOrderLifecycleService.cs");
        var cancelDraft = lifecycleSource.IndexOf("commissions.CancelDraftAsync(", StringComparison.Ordinal); var deleteDraft = lifecycleSource.IndexOf("canceled.ObjectRevision.Value", cancelDraft, StringComparison.Ordinal);
        Assert.True(cancelDraft >= 0 && deleteDraft > cancelDraft);
        Contains(lifecycleSource, "ProfileSyncDeleteExpectation");
        Contains(pageStyles, ".trade-orders-rail-group-title:focus-visible", ".trade-orders-rail-order:focus-visible",
            ".trade-orders-search-result:focus-visible", ".trade-orders-procurement-filter:focus-visible",
            ".trade-orders-timeline-filter:focus-visible",
            ".trade-orders-timeline-visibility button:focus-visible",
            "outline: 2px solid #f0cc62");
        Contains(source,
            "class=\"@($\"{GetRailOrderClass(order)} is-archive\")\"",
            "trade-orders-rail-meta is-date",
            "trade-orders-rail-meta is-outputs",
            "ValueChanged=\"OnOrderSearchChangedAsync\"");
        Contains(pageStyles,
            ".trade-orders-rail-order.is-archive {\n  grid-template-areas:\n    \"title chip\"\n    \"date chip\"\n    \"outputs chip\";\n}",
            ".trade-orders-rail-order.is-archive .trade-orders-rail-meta.is-date {\n  grid-area: date;\n}",
            ".trade-orders-rail-order.is-archive .trade-orders-rail-meta.is-outputs {\n  grid-area: outputs;\n}");
        Contains(pageStyles,
            ".trade-orders-page {\n  box-sizing: border-box;\n  display: flex;\n  flex-direction: column;\n  gap: 8px;\n  height: calc(100vh - 112px);\n  max-width: none !important;\n  padding: 12px 16px;\n  overflow: hidden;\n}",
            ".trade-orders-board {\n  display: grid;\n  flex: 1 1 auto;\n  grid-template-columns: 280px minmax(640px, 1fr) 6px var(--trade-orders-ops-width, clamp(720px, 32vw, 860px));\n  gap: 0;\n  height: auto;\n  min-height: 0;\n}",
            ".trade-orders-workspace {\n  min-height: 0;\n  overflow: hidden;\n}",
            ".trade-orders-workspace-card {\n  display: flex;\n  flex-direction: column;\n  height: 100%;\n  padding: 14px;\n  overflow: auto;\n}",
            "@media (max-width: 1279px) {\n  .trade-orders-page {\n    height: auto;\n    overflow: visible;\n  }");
        Contains(overviewStyles, "container-type: inline-size", "@container (max-width: 620px)",
            ".trade-order-overview__crafter-update,",
            "button.trade-order-overview__step:focus-visible",
            "outline: 2px solid #f0cc62");
        var commission = CreateClearedAssignedCommission();
        var order = new TradeOrder { Title = "Cobalt Joint Plate", Status = TradeOrderStatus.Assigned, CompanyCommission = commission };
        Assert.True(commission.ClearedToWork);
        Assert.Equal(commission.CurrentTermsVersion, commission.ParticipantAcknowledgedTermsVersion);
        Assert.Empty(commission.OutputProgress);
        Assert.DoesNotContain(commission.Activity, item => item.Kind == CompanyCommissionActivityKind.ProgressReported);
        var status = typeof(TradeOrders).GetMethod("FormatWorkbenchStatus", BindingFlags.NonPublic | BindingFlags.Static,
            null, [typeof(TradeOrder), typeof(TradeCompanyCommission)], null)!.Invoke(null, [order, commission]);
        var progress = (IReadOnlyList<TradeOrderProgressStepPresentation>)typeof(TradeOrders)
            .GetMethod("BuildCenterProgress", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [order, commission])!;
        Assert.Equal("Crafting", status);
        Assert.Contains(progress, step => step.Label == "Crafting" && step.State == "Current" && step.IsCurrent);

        var canceled = TradeOrderWorkflow.CopyOrder(order);
        canceled.Status = TradeOrderStatus.Canceled;
        Assert.True(TradeCommissionOperationsPresentation.IsArchivedForAttention(canceled, null));
    }

    [Fact]
    public void DurableCompletedCommissionRemainsTruthfulWithoutOwnerProjection()
    {
        var completed = new TradeOrder
        {
            Status = TradeOrderStatus.Completed,
            CompanyCommission = CreateClearedAssignedCommission() with
            {
                SettlementState = CompanyCommissionSettlementState.Satisfied
            }
        };

        Assert.True(TradeCommissionOperationsPresentation.IsArchivedForAttention(completed, null));
        Assert.Equal(
            TradeCommissionOperationsPresentation.DeliveryAttention,
            TradeCommissionOperationsPresentation.GetAttentionGroup(completed));

        completed.CompanyCommission = completed.CompanyCommission with
        {
            SettlementState = CompanyCommissionSettlementState.NotDue
        };

        Assert.False(TradeCommissionOperationsPresentation.IsArchivedForAttention(completed, null));
        Assert.Equal(
            TradeCommissionOperationsPresentation.DeliveryAttention,
            TradeCommissionOperationsPresentation.GetAttentionGroup(completed));
    }

    [Fact]
    public void DurableCommissionClassificationMatchesOwnerProjection()
    {
        var order = new TradeOrder
        {
            Status = TradeOrderStatus.Assigned,
            CompanyCommission = CreateClearedAssignedCommission()
        };
        var projection = new CompanyCommissionOwnerProjection
        {
            Order = order,
            ObjectRevision = new CompanyRecordRevision(12),
            CompanyRevision = new CompanyRecordRevision(12)
        };

        Assert.Equal(
            TradeCommissionOperationsPresentation.GetAttentionGroup(projection),
            TradeCommissionOperationsPresentation.GetAttentionGroup(order));
        Assert.Equal(
            TradeCommissionOperationsPresentation.IsArchivedForAttention(order, projection),
            TradeCommissionOperationsPresentation.IsArchivedForAttention(order, null));
    }

    [Fact]
    public void LifecycleEntryPrioritizesVerificationWithoutReplayingWorkingTerms()
    {
        var repositoryRoot = LocateRepositoryRoot();
        var page = ReadWebSource(repositoryRoot, "Pages", "TradeOrders.razor.cs");
        var payment = ReadWebSource(repositoryRoot, "Pages", "TradeOrders.Payment.cs");
        var restoration = ReadWebSource(repositoryRoot, "Pages", "TradeOrders.Restoration.cs");
        var lifecycle = ReadWebSource(
            repositoryRoot,
            "Services",
            "TradeCompany",
            "TradeOrderLifecycleService.cs");

        Omits(page, "HostedOrders.VerificationChanged +=", "OnHostedOrderVerificationChanged");
        Contains(
            payment,
            "await EnsureSelectedCommissionOwnerAvailableAsync()",
            "OpenCloseOrderDialogAsync(TradeOrderStatus.Canceled)");
        Contains(
            restoration,
            "await HostedOrderSync.RefreshOwnerProjectionAsync(selected.Id)",
            "Opening the lifecycle dialog is not a mutation");
        Contains(
            lifecycle,
            "var owner = commissions.GetForOrder(order.Id);",
            "if (owner == null)",
            "await commissions.RefreshAsync(order, cancellationToken);");
    }

    private static TradeCompanyCommission CreateClearedAssignedCommission()
    {
        var lineId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var actor = new CompanyCommissionActor("commissioner", CompanyCommissionActorKind.Commissioner);
        return new()
        {
            CommissionId = Guid.Parse("66666666-6666-6666-6666-666666666666"),
            CompanyId = new(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            CommissionerActorId = actor.ActorId,
            Reference = "CA-ZERO-PROGRESS",
            CreatedAtUtc = CapturedAt,
            UpdatedAtUtc = CapturedAt,
            CurrentTermsVersion = 1,
            TermsVersions = [new CompanyCommissionTermsVersion
            {
                Version = 1, CreatedAtUtc = CapturedAt, CreatedBy = actor,
                Outputs = [new(lineId, 1, "Cobalt Joint Plate", 10, false)],
                Payment = new(CompanyCommissionPaymentSchedule.Advance, "Labor standard", 0, 0, 1_000, 1_000),
                PricingEvidence = new("Selected routes", "Aether", "Siren", CapturedAt)
            }],
            PublicMetadata = new() { PublicBriefId = "zero-progress", ViewState = CompanyCommissionPublicViewState.Published },
            ActiveClaimCapabilityRevision = 1,
            ActiveClaim = new(Guid.Parse("77777777-7777-7777-7777-777777777777"), 1, CapturedAt, Guid.Parse("88888888-8888-8888-8888-888888888888"), null),
            ParticipantAcknowledgedTermsVersion = 1,
            Gates = new(new(CompanyCommissionClearanceState.Satisfied), new(CompanyCommissionClearanceState.Satisfied),
                new(CompanyCommissionClearanceState.NotRequired, [])),
            DeliveryReadiness = new(false),
            SettlementState = CompanyCommissionSettlementState.NotDue,
            OutputProgress = [],
            Activity = []
        };
    }
    private static void AssertPublication(TradeOrderStatus status, string[] contains, params string[] omits)
    {
        var payload = SerializePublication(CreateBrief(status, clearedToWork: true));
        Contains(payload, contains);
        Omits(payload, omits);
    }
    private static void Contains(string source, params string[] values) =>
        Array.ForEach(values, value => Assert.Contains(value, source, StringComparison.Ordinal));
    private static void Omits(string source, params string[] values) =>
        Array.ForEach(values, value => Assert.DoesNotContain(value, source, StringComparison.Ordinal));
    private static string SerializePublication(CompanyCommissionPublicBrief brief) =>
        JsonSerializer.Serialize(
            CompanyCommissionDiscordMessage.CreatePublication(new(
                new CompanyId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
                brief, new CompanyRecordRevision(12), Guid.Parse("22222222-2222-2222-2222-222222222222"), 12,
                CompanyCommissionActivityKind.ProgressReported, CapturedAt,
                "Progress updated.", new Uri("https://example.test/commission"), null)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
    private static CompanyCommissionPublicBrief CreateBrief(TradeOrderStatus status, bool clearedToWork)
    {
        var lineId = Guid.Parse("33333333-3333-3333-3333-333333333333");
        return new()
        {
            PublicBriefId = "projection-contract",
            CommissionId = Guid.Parse("44444444-4444-4444-4444-444444444444"),
            Title = "Cobalt Joint Plate",
            CompanyDisplayName = "Test Company",
            Reference = "CA-PROGRESS",
            ViewState = CompanyCommissionPublicViewState.Published,
            Terms = new()
            {
                Version = 4,
                Outputs = [new(lineId, 1, "Cobalt Joint Plate", 10, false)],
                Payment = new(CompanyCommissionPaymentSchedule.Advance, "Labor standard", 1_000, 0, 200, 1_200),
                PricingEvidence = new("Selected routes", "Aether", "Siren", CapturedAt)
            },
            Status = status,
            Gates = new(CompanyCommissionClearanceState.Satisfied, CompanyCommissionClearanceState.Satisfied, CompanyCommissionClearanceState.NotRequired),
            ClearedToWork = clearedToWork,
            IsClaimed = true,
            OutputProgress = [new(lineId, 1, 10, 3, 2, 0, CapturedAt)],
            DeliveryReadiness = new(status == TradeOrderStatus.AwaitingDelivery, status == TradeOrderStatus.AwaitingDelivery ? CapturedAt : null, null),
            SettlementState = CompanyCommissionSettlementState.NotDue,
            Closed = false,
            ProjectionRevision = 12
        };
    }
    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "FFXIV Craft Architect.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private static string ReadWebSource(string repositoryRoot, params string[] relativePath) =>
        File.ReadAllText(Path.Combine(repositoryRoot, "src", "FFXIV Craft Architect.Web", Path.Combine(relativePath)));
}

public sealed class CompanyCommissionTermsRevisionConflictPolicyTests
{
    [Theory]
    [InlineData(10, 3, 10, 3, false, false)]
    [InlineData(10, 3, 11, 3, false, false)]
    [InlineData(10, 3, 10, 4, false, false)]
    [InlineData(10, 3, 11, 3, true, false)]
    [InlineData(10, 3, 10, 4, true, true)]
    [InlineData(11, 4, 11, 4, true, false)]
    public void ConflictRequiresDirtyChangesAgainstNewerTerms(
        long baseObjectRevision,
        int baseTermsVersion,
        long currentObjectRevision,
        int currentTermsVersion,
        bool hasLocalChanges,
        bool expectedConflict)
    {
        Assert.Equal(
            expectedConflict,
            CompanyCommissionTermsRevisionConflictPolicy.HasConflict(
                new CompanyCommissionTermsRevisionBase(
                    new CompanyRecordRevision(baseObjectRevision),
                    baseTermsVersion),
                new CompanyRecordRevision(currentObjectRevision),
                currentTermsVersion,
                hasLocalChanges));
    }
}
