using FFXIV_Craft_Architect.Core.Models;
using FFXIV_Craft_Architect.Core.Services;
using FFXIV_Craft_Architect.LodestoneLookup.Services.ProfileHosting;

namespace FFXIV_Craft_Architect.LodestoneLookup.Services.TradeCompanies;

public sealed class ClaimMaterialPricing(
    SqliteProfileHostStore profiles, IMarketCacheService cache, UniversalisService universalis, TimeProvider clock)
{
    internal async Task<AmendCompanyCommissionTermsCommand> PriceAsync(
        TradeCompanyAccessContext access, TradeOrder order, CompanyCommissionCommandContext context, CancellationToken ct)
    {
        var terms = order.CompanyCommission!.CurrentTerms;
        var old = terms.PricingEvidence.MaterialQuote!;
        var snapshot = TradeOrderWorkflow.CopySourceSnapshot(order.SourceSnapshot);
        if (access.HostProfileId is not { } profile || order.CraftPlanId is not { } id ||
            old.SourcePlanId != id || old.PlanSessionVersion != snapshot.PlanSessionVersion ||
            old.MarketAnalysisVersion != snapshot.MarketAnalysisVersion ||
            snapshot.DataCenter is not { Length: > 0 } dc || snapshot.Region is not { Length: > 0 } region ||
            snapshot.MarketFetchScope is not { } scope || snapshot.RequestedDataCenters.Count == 0)
        {
            throw new InvalidOperationException("Missing or inconsistent linked plan or market scope.");
        }
        var record = await profiles.LoadObjectAsync(profile.ToString("D"), ProfileSyncCollections.Plans, id, ct);
        var stored = record is { Deleted: false } ? ProfileSyncPlanPayloadCodec.Deserialize(record.PayloadJson, id) : null;
        if (stored?.PlanJson == null || stored.PlanStateJson == null || stored.LinkedOrderId != order.Id ||
            stored.SavedAt != order.CraftPlanSavedAtUtc)
        {
            throw new InvalidOperationException("The exact linked plan and acquisition choices are unavailable.");
        }
        var plan = CorePlanSessionLoadService.RestorePlan(stored.PlanJson, stored.PlanStateJson, requireComplete: true)
            ?? throw new InvalidOperationException("Empty linked plan.");
        var projector = new RecipeDemandProjectionService();
        var initial = projector.Build(plan, null);
        var stockOnlyIds = initial.AllPlanDemand.GroupBy(row => row.ItemId)
            .Where(group => group.All(row => row.Source == AcquisitionSource.OnHand))
            .Select(group => group.Key).ToHashSet();
        var candidates = initial.ToMarketAnalysisMaterialAggregates().Where(item => !stockOnlyIds.Contains(item.ItemId)).ToList();
        IReadOnlyList<DetailedShoppingPlan> plans = [];
        if (candidates.Count > 0)
        {
            var pairs = candidates.SelectMany(item => snapshot.RequestedDataCenters.Select(center => (item.ItemId, center))).ToList();
            if (await cache.RefreshRequestedAsync(pairs, ct: ct) != pairs.Count)
            {
                throw new InvalidOperationException("Market data incomplete for the commission pricing scope.");
            }
            var worlds = await universalis.GetWorldDataAsync(ct);
            var ladder = new MarketPriceLadderAnalysisService();
            var evidence = await new MarketEvidenceReconciliationService(new MarketAnalysisExecutionService(cache, ladder))
                .ReconcileAsync(new MarketEvidenceReconciliationRequest
                {
                    Items = candidates,
                    Scope = scope,
                    SelectedRegion = region,
                    SelectedDataCenter = dc,
                    RequestedDataCenters = snapshot.RequestedDataCenters,
                    Lens = stored.SavedMarketAnalysisLens,
                    ExpectedWorldsByDataCenter = snapshot.RequestedDataCenters.ToDictionary(
                        center => center, center => (IReadOnlyList<string>)worlds.DataCenterToWorlds[center]),
                    CacheAlreadyPopulated = true
                }, ct: ct);
            if (evidence.UnavailableItemIds.Count != 0)
            {
                throw new InvalidOperationException("Market analysis is unavailable for one or more plan materials.");
            }
            plans = evidence.ShoppingPlans;
            AcquisitionPlanningService.ReconcileAcquisitionDecisions(plan, plans);
        }
        var demand = projector.Build(plan, null);
        var items = demand.ToSelectedProcurementMaterialAggregates();
        var sources = demand.ActiveProcurementDemand.Where(row => row.Quantity > 0)
            .GroupBy(row => (row.ItemId, row.MustBeHq))
            .ToDictionary(group => group.Key, group => group.Select(row => row.Source).Distinct().Single());
        var now = clock.GetUtcNow().UtcDateTime;
        var result = await new TradeMaterialQuoteService().PriceAsync(new MarketShoppingService(cache),
            plans, items, sources, old.AppliedPolicy, now,
            new CommissionCostBasisResolver().BuildSelectedSourceLines(demand.ActiveProcurementDemand, [], plans), ct);
        var quote = result.Quote ?? throw new InvalidOperationException(result.FailureReason);
        if (string.IsNullOrWhiteSpace(quote.RouteSelectionKey))
        {
            throw new InvalidOperationException("The optimizer returned no publishable route selection key.");
        }
        var lines = result.MaterialLines.ToDictionary(line => (line.ItemId, line.RequiresHq));
        var published = terms.Materials.ToDictionary(m => (m.ItemId, m.RequiresHq));
        var crafter = terms.Materials.Where(m => m.Responsibility == CommissionMaterialResponsibility.Crafter)
            .Select(m => (m.ItemId, m.RequiresHq)).ToHashSet();
        if (!crafter.IsSubsetOf(lines.Keys) || lines.Any(pair =>
            !published.TryGetValue(pair.Key, out var material) || material.Quantity != pair.Value.Quantity))
        {
            throw new InvalidOperationException("Refreshed demand does not exactly match the published materials.");
        }
        var fullQuote = quote with
        {
            CompanyProfileId = old.CompanyProfileId,
            SourcePlanId = id,
            PlanSessionVersion = old.PlanSessionVersion,
            MarketAnalysisVersion = checked(old.MarketAnalysisVersion + 1)
        };
        quote = TradePaymentCalculator.ProjectCrafterQuote(fullQuote, crafter);
        var materials = terms.Materials.Select(m =>
        {
            if (m.Responsibility == CommissionMaterialResponsibility.Provided)
            {
                return m;
            }
            var line = lines[(m.ItemId, m.RequiresHq)];
            return m with { UnitCost = line.UnitCost, TotalCost = decimal.Round(line.UnitCost * m.Quantity, 0, MidpointRounding.AwayFromZero) };
        }).ToArray();
        var originalLines = snapshot.Materials.ToDictionary(m => (m.ItemId, m.RequiresHq));
        snapshot.Materials = materials.Select(m =>
        {
            if (m.Responsibility == CommissionMaterialResponsibility.Provided)
            {
                return originalLines.GetValueOrDefault((m.ItemId, m.RequiresHq))
                    ?? new TradeOrderMaterialSnapshot(m.ItemId, m.Name, m.Quantity, m.RequiresHq, m.UnitCost, m.TotalCost);
            }
            var line = lines[(m.ItemId, m.RequiresHq)];
            return new TradeOrderMaterialSnapshot(m.ItemId, m.Name, m.Quantity, m.RequiresHq, m.UnitCost, m.TotalCost,
                line.EvidenceSource, line.UnitCostExplanation, line.EvidenceTimestampUtc, line.Warnings);
        }).ToArray();
        snapshot.MaterialQuote = fullQuote;
        snapshot.MarketAnalysisVersion = quote.MarketAnalysisVersion;
        snapshot.ImportedAtUtc = now;
        var reimbursement = quote.MaterialReimbursement;
        var revised = terms with
        {
            Version = checked(terms.Version + 1),
            Materials = materials,
            PricingEvidence = terms.PricingEvidence with { MaterialQuote = quote, CapturedAtUtc = now },
            Payment = terms.Payment with
            {
                HistoricalAmount = 0,
                MaterialReimbursement = reimbursement,
                Total = terms.Payment.CraftLabor + reimbursement
            }
        };
        return new(context with { CommandId = Guid.NewGuid() }, revised, "Material prices were refreshed at claim time.",
            new(snapshot.RootItems.Select(m => new TradeRequestedOrderOutput(m.ItemId, m.Name, m.Quantity, m.MustBeHq, m.EstimatedSaleValue)).ToArray(),
                snapshot, id, order.CraftPlanName, order.CraftPlanSavedAtUtc, order.CraftPlanLinkKind));
    }
}
