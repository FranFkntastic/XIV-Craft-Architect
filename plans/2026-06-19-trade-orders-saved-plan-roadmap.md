# Trade Orders Saved Plan Roadmap

Date: 2026-06-19
Branch: company-profile-work

## Goal

Collapse Trade-specific craft-plan snapshot behavior into normal Craft Architect saved plans.

Trade orders are the durable operations object. Craft Architect saved plans are the durable recipe artifact. Market analysis and procurement evidence are refreshed when payment needs to be calculated.

## Ownership

Trade Order owns:

- title, status, assigned crafter, commissioned date, notes, and history
- requested outputs
- material responsibility and payment evidence copied back to the order
- a link to the order's saved Craft Architect plan

Craft Architect saved plan owns:

- project items
- recipe tree / plan JSON
- user recipe decisions
- buy/craft choices
- saved-plan identity and metadata

Market analysis owns:

- current market item analyses
- shopping plans
- acquisition evidence
- vendor/market price evidence
- payment-readiness inputs

Trade must not duplicate recipe building, market pricing, vendor pricing, or payment math.

## Saved Plan Direction

When an order creates or replaces its craft plan:

1. Build through the Craft Architect recipe pipeline from the order requested outputs.
2. Save the generated plan as a normal `StoredPlan`.
3. Omit volatile market-analysis payloads from generated order plans.
4. Link the order to the saved plan.
5. Mark the link as order-generated before allowing replace-in-place.
6. Open the linked saved plan through normal plan loading.

Unknown or external linked plan IDs must not be overwritten. If an order has an unknown link kind, replacing should create a new order-generated saved plan and relink the order.

## Model Shape

`TradeOrder` should carry explicit linked-plan fields:

```csharp
public string? CraftPlanId { get; set; }
public string? CraftPlanName { get; set; }
public DateTime? CraftPlanSavedAtUtc { get; set; }
public TradeOrderCraftPlanLinkKind CraftPlanLinkKind { get; set; }
```

`TradeOrderSourceSnapshot.SourcePlanId` remains import provenance, not the durable order plan link.

## UI Language

Replace snapshot language with linked-plan language:

- `Linked Craft Plan`
- `Create Craft Plan`
- `Replace Craft Plan`
- `Open Craft Plan`
- `Pricing evidence missing`
- `Priced evidence available`

Recipe-plan creation should not imply payment evidence is ready. A material breakdown is not the same thing as priced procurement evidence.

## Acceptance

- New orders can represent `no linked craft plan`.
- Creating an order-generated plan does not overwrite the user's current saved plan.
- Replacing reuses only links marked `OrderGenerated`.
- Generated order plans reopen through normal Craft Architect load paths.
- Generated order plans omit stale market-analysis payloads.
- No new writes go to Trade-specific craft snapshot storage.
- Payment readiness requires priced procurement evidence, not only material rows.

## Deferred Work

Do not delete legacy `tradeOrderCraftSnapshots` storage in this pass. Keep old records recoverable until the saved-plan workflow has survived real use.

The remaining major workflow slice is pricing evidence refresh:

1. Load the linked saved plan.
2. Run the existing Craft Architect market/procurement workflow.
3. Convert existing acquisition evidence into `TradeOrderMaterialSnapshot` rows.
4. Save refreshed evidence back to the order.

That slice is specified separately in `2026-06-19-trade-order-pricing-evidence.md`.
