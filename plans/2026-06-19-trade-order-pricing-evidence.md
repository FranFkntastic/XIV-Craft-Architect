# Trade Order Pricing Evidence Design

Date: 2026-06-19
Branch: company-profile-work

## Goal

Add an Orders workflow action that refreshes procurement pricing evidence for a linked order craft plan and saves the resulting payment evidence back to the order.

This is not a second market-analysis implementation. Trade Architect should orchestrate existing Core/Craft Architect services, then persist the order-facing evidence needed for operations.

## Non-Goals

- Do not duplicate recipe tree traversal.
- Do not duplicate market-price selection.
- Do not duplicate vendor-price handling.
- Do not duplicate commission or payment math.
- Do not make order craft-plan creation imply payment evidence is ready.

## Ownership Boundary

Core / Craft Architect should own:

- loading a saved plan into a plan/session model
- recipe plan construction
- recipe layer/procurement projection
- market-analysis refresh
- acquisition recommendation generation
- vendor and market cost-basis resolution
- evidence warnings and staleness signals

Trade Architect should own:

- choosing the target order
- requiring a linked saved plan
- invoking the existing pricing workflow
- mapping returned acquisition evidence into `TradeOrderMaterialSnapshot`
- preserving material responsibility choices
- saving the updated order
- showing clear status/failure feedback

If implementation starts adding new code that chooses prices, walks recipe trees, or calculates payment totals, it is crossing the boundary.

## User Workflow

1. Operator opens `Trade > Orders`.
2. Operator selects an order.
3. If no linked craft plan exists, the page offers `Create Craft Plan`.
4. If a linked craft plan exists, the page offers `Refresh Pricing Evidence`.
5. Refresh loads the linked saved plan, runs existing market/procurement analysis, and updates the order evidence.
6. Payment amount, procurement total, warnings, and material responsibility immediately reflect the saved evidence.

Suggested UI labels:

- `Refresh Pricing Evidence`
- `Pricing evidence missing`
- `Pricing evidence refreshed`
- `Pricing partially refreshed`
- `Open Craft Plan`

## Service Shape

Add a thin Trade orchestration service, tentatively:

```csharp
public sealed class TradeOrderPricingEvidenceService
{
    public Task<TradeOrderPricingEvidenceResult> RefreshAsync(
        TradeOrder order,
        TradePayrollWorkflowDraft? draft,
        CancellationToken ct = default);
}
```

The service should compose existing infrastructure rather than replace it:

- `WebPlanPersistenceService` or the underlying plan store to load the linked `StoredPlan`
- `PlanSessionLoadService` to prepare the saved plan
- existing market-analysis/procurement workflow services to refresh acquisition evidence
- existing cost-basis/payment models to produce material evidence

The result should include:

- updated material evidence rows
- warnings
- evidence timestamp/version metadata where available
- clear failure reason if no linked plan, missing plan, no price data, or partial evidence

## Evidence Mapping

`TradeOrderMaterialSnapshot` remains the order-facing evidence record:

- item id/name
- quantity
- HQ requirement
- unit cost
- total cost
- evidence source
- unit-cost explanation
- evidence timestamp
- warnings

Only priced acquisition evidence should count as payment-ready. Recipe material rows with zero price may be useful as a breakdown, but they must not mark payment evidence as complete.

## Persistence

On refresh success:

1. Copy the selected order.
2. Replace `SourceSnapshot.Materials` with refreshed evidence rows.
3. Preserve saved payroll/material responsibility choices by item id and HQ flag.
4. Update order timestamp and history.
5. Save through `TradeOperationsPersistenceService`.
6. Reload/reselect the order before showing success.

If order save fails after analysis succeeds, do not mutate the loaded selected order in memory. Show an explicit error.

## Tests

Add focused tests that prove:

- the Orders page exposes refresh pricing only when a linked craft plan exists
- missing linked plans fail clearly
- generated recipe material rows alone do not mark payment as ready
- refresh uses existing plan/procurement/cost-basis services
- vendor-priced items use vendor evidence
- market-priced items use acquisition evidence
- responsibility choices survive evidence refresh
- save failure does not mutate the selected order before persistence succeeds

## Open Design Point

The refresh action can run without visibly navigating to Craft Architect, but the user still needs an `Open Craft Plan` path for inspection and manual tuning.

The preferred first implementation is silent orchestration from Orders using existing services. Navigate only when the user explicitly opens the plan.
