# Trade Architect Operations Roadmap

Date: 2026-06-20
Branch: local-dev

## Current Direction

Trade Architect is an order-first operations board for informal crafting companies.

The durable objects are:

- `TradeCompanyProfile` for local company context.
- `TradeCrafterProfile` for roster and crafting job levels.
- `TradeOrder` for requested work, assignment, lifecycle, history, and order-facing payment evidence.
- normal Craft Architect saved plans for recipe trees and user craft/procurement decisions.

Trade orders link to saved Craft Architect plans. They do not own a separate craft-plan snapshot model anymore.

## Boundaries

Craft Architect / Core owns:

- recipe plan construction
- recipe-layer and procurement projection
- market/procurement evidence refresh
- vendor and market cost-basis resolution
- payment math primitives

Trade Architect owns:

- order and crafter operations workflow
- assignment, status, notes, and history
- requested output capture
- material responsibility selection
- mapping refreshed evidence into order-visible material rows

If Trade code starts walking recipe trees, selecting prices, applying vendor costs, or calculating commission math independently, it is crossing the boundary.

## Orders Workflow

The Orders page is the main operational surface:

- left rail: searchable grouped orders, selected order, collapsed archive
- center: selected/new order editor, requested outputs, linked craft plan controls
- right rail: Payment, Procurement, and History tabs

Important language:

- `Linked Craft Plan`
- `Create Craft Plan`
- `Replace Craft Plan`
- `Open Craft Plan`
- `Refresh Pricing Evidence`
- `Pricing evidence missing`
- `Assigned Awaiting Payment`
- `Awaiting Delivery`

Payment readiness requires priced procurement evidence. A recipe material breakdown alone is not enough.

## Saved Plan Rules

When an order creates or replaces its craft plan:

1. Build through the existing Craft Architect recipe pipeline from requested outputs.
2. Save as a normal `StoredPlan`.
3. Omit volatile market-analysis payloads from generated order plans.
4. Link the order to the saved plan with `CraftPlanId`, `CraftPlanName`, `CraftPlanSavedAtUtc`, and `CraftPlanLinkKind`.
5. Replace in place only for links marked `OrderGenerated`.
6. Open linked plans through normal plan loading.

Unknown/external linked plans should not be overwritten.

## Pricing Evidence Refresh

`Refresh Pricing Evidence` runs silently from Orders:

1. Load the linked saved plan.
2. Prepare the plan through `PlanSessionLoadService`.
3. Build active procurement items through `IRecipeLayerWorkflowService`.
4. Run `IProcurementRouteExecutionService`.
5. Resolve payment material rows through `CommissionCostBasisResolver`.
6. Map rows into `TradeOrderMaterialSnapshot`.
7. Save the updated order and add pricing history.

This action refreshes evidence only; it should not imply the user reviewed the market-analysis screen.

## Deferred Decisions

- Whether the top-level Payroll route should be removed once Orders payment is fully trusted.
- Whether material responsibility should move directly onto `TradeOrder` instead of remaining backed by payroll drafts.
- Whether requested output item selection should eventually share a richer Craft Architect picker.
- Whether Lodestone character lookup should populate crafter profiles.
- Whether local company profile import/export is worth adding before hosted sync.

## Cleanup Notes

Older design notes used `snapshot` language while the feature was still experimental. The current model is linked saved plans plus refreshed order-facing payment evidence.
