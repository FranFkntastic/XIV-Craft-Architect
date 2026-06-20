# Trade Order-First Workflow Implementation Plan

> Superseded storage note, 2026-06-19: this plan's `TradeOrderCraftSnapshot` language was an intermediate design. Current direction is to use normal Craft Architect saved plans for order craft plans, with Trade orders storing linked-plan identity and order-facing pricing evidence. See `2026-06-19-trade-orders-saved-plan-roadmap.md` and `2026-06-19-trade-order-pricing-evidence.md`.

## Goal

Move Trade Architect toward an order-first operations workflow where orders are the durable object, requested outputs are the entry point, and payment/payroll becomes part of the selected order workflow rather than a separate top-level destination.

The implementation should preserve one recipe pipeline: Trade must route requested outputs through Craft Architect plan construction instead of inventing a second recipe builder.

## Product Shape

- `Orders` is the primary Trade Architect page.
- `Crafters` remains a separate roster/capability page.
- The current `Payroll` page is gradually demoted into an order detail mode/section, likely renamed `Payment` or `Settlement`.
- Users can create an order inside Trade Architect without first building an active Craft Architect plan.
- Users can still create an order from the active Craft Architect plan.
- Opening or refreshing an order's craft plan should rebuild through the same Craft Architect internals used by the normal planner.

## Non-Goals

- Do not create a second independent recipe construction path.
- Do not store market analysis payloads inside order craft snapshots.
- Do not remove the existing payroll page in the first production slice unless the replacement order detail flow is already usable.
- Do not add hosted sync, Lodestone import, or company-profile export/import in this pass.

## Target Workflow

1. Operator opens `Trade > Orders`.
2. Operator clicks `New Order`.
3. Operator enters or confirms:
   - order title
   - requested output items
   - quantities
   - HQ requirements
   - assigned crafter, optional
   - notes, optional
4. Trade creates a durable order with captured requested outputs.
5. Trade asks the Craft Architect pipeline to build a craft plan from those requested outputs.
6. Trade saves a lean order-owned craft snapshot.
7. Operator can proceed into:
   - procurement / market analysis review
   - material responsibility
   - payment calculation
   - assignment/status tracking

## Architecture Direction

### Durable Order Model

`TradeOrder` remains the durable aggregate.

Extend or clarify `TradeOrderSourceSnapshot` so it can represent a Trade-native order request, not only an import from the active craft plan:

- `SourceKind`: suggested enum, values like `ActiveCraftPlan`, `TradeRequestedOutputs`, `ImportedExternal`
- `SourcePlanName`
- `SourcePlanId`
- `OrderCraftSnapshotId`
- `DataCenter`
- `RootItems`
- `Materials`
- `ImportedAtUtc`
- existing version fields remain for active-plan imports

Avoid renaming existing fields in this pass unless migration impact is very small.

### Requested Outputs

Use the existing `TradeOrderRootItemSnapshot` shape for captured order outputs if sufficient:

- `ItemId`
- `Name`
- `Quantity`
- `MustBeHq`
- `EstimatedSaleValue`

If UI editing needs icon/search metadata, introduce a UI draft type rather than bloating the durable model.

### Craft Plan Construction

Create a shared service method that represents the Craft Architect pipeline boundary.

Candidate:

```csharp
public sealed class CraftPlanBuildWorkflowService
{
    Task<CraftPlanBuildResult> BuildFromRequestedOutputsAsync(
        IReadOnlyList<RequestedCraftOutput> outputs,
        string dataCenter,
        string world,
        CancellationToken ct = default);
}
```

This service should call the same internals the Craft Architect planner uses today, likely wrapping `RecipeCalculationService.BuildPlanAsync(...)` at first.

Consumers:

- Craft Architect build button
- Trade order creation from requested outputs
- Trade order snapshot rebuild/replace
- Future imports that produce requested outputs

The important boundary is that Trade supplies intent; Craft Architect builds the plan.

### Order Craft Snapshots

Continue using `TradeOrderCraftSnapshot`.

Rules:

- Store one lean snapshot per order by default.
- Snapshot contains plan JSON, project/root items, data center, and metadata.
- Snapshot excludes market analysis, market intelligence, shopping plans, market cache, and procurement overlays.
- Rebuild/replace snapshots from the order's requested outputs, not from the global active plan.
- Replacement must be explicit and must clean up the old snapshot only after the order link saves.

## UI Plan

### Orders Page Layout

Move toward the mockup's three-pane shape:

- left: collapsible order groups and search
- center: selected/new order editor and requested outputs
- right: selected order detail tabs

Detail tabs:

- `Payment`
- `Procurement`
- `History`

`Payment` contains the current payroll logic.

### New Order Flow

Add a Trade-native creation panel or dialog:

- `Order title`
- `Assigned crafter`
- requested outputs table
- `Add item`
- `Paste list`, optional later
- `Build / Refresh Craft Plan`
- `Save Order`

Minimum first pass can use a compact inline form on the Orders page.

### Item Selection

Use existing item search/selection primitives where possible.

If no reusable picker exists, first pass can add a narrow search field backed by existing Garland item search, but the implementation should be isolated so it can later share the Craft Architect project-item picker.

### Payment Section

Move or reuse current payroll calculations inside the selected order detail.

The payment section should show:

- payment amount, large
- procurement estimate
- material responsibility table
- copy payment amount
- copy payroll summary

Persist responsibility using the existing payroll draft persistence initially, but make the UI look order-owned.

## Implementation Slices

### Slice 1: Shared Plan Build Boundary

- Add `CraftPlanBuildWorkflowService` or equivalent.
- Route current Trade snapshot rebuild through this service.
- Keep existing active-plan order creation behavior unchanged.
- Add tests proving rebuild uses the shared boundary and not active `AppState.CurrentPlan`.

Verification:

- focused Trade tests
- Web build

### Slice 2: Trade-Native Order Draft Model

- Add a draft model for requested outputs.
- Add conversion from draft outputs to `TradeOrder`.
- Populate `TradeOrderSourceSnapshot.RootItems` from the draft.
- Store `SourceKind` or equivalent marker.
- Capture data center at creation time.

Verification:

- unit tests for draft-to-order conversion
- existing active-plan import tests still pass

### Slice 3: Order Creation UI

- Add `New Order` or inline creation panel on `TradeOrders.razor`.
- Support title, assigned crafter, output rows, quantity, HQ.
- Use existing item lookup if available; otherwise isolate temporary lookup.
- Save durable order after successful plan build/snapshot save.
- Roll back snapshot if order save fails.

Verification:

- markup/source tests for requested outputs UI
- focused Trade tests
- Web build

### Slice 4: Payment as Order Detail

- Add `Payment` tab/section to order detail pane.
- Reuse `TradeCommissionPaymentSummary` and existing payroll draft responsibility persistence.
- Keep top-level Payroll page available during transition.
- Add navigation from old Payroll page to selected order payment flow if a linked order exists.

Verification:

- payment display and copy tests
- responsibility persistence tests

### Slice 5: Procurement and Market Analysis Hooks

- Add clear action from an order snapshot to open Craft Architect / Market Analysis for the order plan.
- Do not store market analysis in the order snapshot.
- After opening the order plan, market analysis runs fresh.

Verification:

- opening order plan clears saved-plan identity
- market payload fields remain null in order snapshots

### Slice 6: De-emphasize Payroll Tab

After order detail payment is stable:

- Rename top-level `Payroll` tab to `Payment` only if still needed.
- Or remove the top-level tab and keep payment under Orders.
- Update layout tests and navigation semantics.

This should be a separate decision point, not bundled into the first implementation.

## Migration Notes

Existing orders fall into three categories:

- Active-plan imports with existing snapshots.
- Active-plan imports without snapshots.
- Orders with payroll drafts but no robust order-owned plan.

Behavior:

- Existing orders remain loadable.
- Missing snapshots can be rebuilt from `SourceSnapshot.RootItems`.
- Existing payroll drafts continue linking by `OrderId`, `PayrollDraftId`, or legacy plan-session matching.
- No destructive migration is required.

## Risks

- Item search in Trade could accidentally duplicate Craft Architect project-item UI.
- Payment persistence may feel split if payroll drafts remain technically separate too long.
- Rebuilding a plan from old order outputs can produce a different recipe tree if recipe/source data changed.
- Large plans can still grow IndexedDB; lean snapshots control but do not eliminate storage growth.
- Moving the top-level Payroll tab too early could make active-plan calculator workflows harder before the order detail replacement is complete.

## Open Questions

- Should `New Order` require a successful craft plan build before saving, or allow a request-only draft?
- Should payment responsibility be stored directly on `TradeOrder` eventually, replacing payroll draft persistence?
- Should requested output item selection reuse the current Craft Architect project-item panel directly, or be a compact Trade-specific picker backed by the same service?
- Should `Ready to Scope` replace `Ready to Assign`, or should status semantics wait until the payment workflow is merged?
- Should manually created orders immediately navigate to payment/procurement, or remain in an editable request state?

## Recommended First Commit

Start with Slice 1 only:

- Introduce the shared Craft Architect plan-build boundary.
- Route order snapshot rebuild through it.
- Leave UI mostly unchanged.

This removes the architectural smell before larger UI work starts.
