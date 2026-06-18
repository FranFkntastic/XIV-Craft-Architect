# Trade Payroll Workflow Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fold the payroll page into the Trade operations workflow by replacing commissioner-name entry with assigned-crafter selection, persisting payroll responsibility edits, and pricing payroll materials from concrete acquisition evidence instead of market averages.

**Architecture:** Payroll becomes a durable Trade workflow draft tied to the active company profile and, when possible, a Trade order. The page still regenerates material/evidence rows from the current craft plan so market data stays fresh, then overlays saved workflow choices such as assigned crafter and per-material responsibility. Price basis moves into `CommissionCostBasisResolver` by reusing `MarketPurchaseCostProjectionService.Estimate(...)` before falling back to market-analysis aggregate prices.

**Tech Stack:** Blazor WebAssembly, MudBlazor, browser IndexedDB interop, FFXIV Craft Architect Core service tests, xUnit.

---

## Current Findings

- `src/FFXIV Craft Architect.Web/Pages/TradePayroll.razor` rebuilds `_source`, `_payroll`, and `_lines` from the active craft plan on initialization. Responsibility edits live only in component memory.
- `BuildPayrollSummary()` in `TradePayroll.razor` owns the copied payroll text, so visible wording and clipboard wording must be changed together.
- `src/FFXIV Craft Architect.Core/Services/CommissionCostBasisResolver.cs` chooses competitive average / scope average / median / baseline before anything else. Shopping plans are currently used for recommendation-age warnings, not unit cost.
- `src/FFXIV Craft Architect.Core/Services/MarketPurchaseCostProjectionService.cs` already returns supported acquisition estimates and handles vendor recommendations via `MarketShoppingConstants.VendorWorldName`.
- `src/FFXIV Craft Architect.Core/Models/TradeOperationsModels.cs` already has `TradeOrder.PayrollDraftId` as `string?` and `TradeOrderHistoryEventKind.PayrollLinked`, so order/payroll linkage should use those existing hooks and store payroll draft ids in canonical string form.
- `src/FFXIV Craft Architect.Web/wwwroot/indexedDB.js` is on `DB_VERSION = 6` and repairs missing Trade stores. Any new payroll store must update the required-store diagnostics and repair path to avoid repeating the previous partial-migration failure mode.

## File Map

- Create `src/FFXIV Craft Architect.Core/Models/TradePayrollWorkflowModels.cs`: durable payroll draft model, saved line responsibility model, sync fields.
- Modify `src/FFXIV Craft Architect.Core/Models/TradeOperationsModels.cs`: keep existing `PayrollDraftId`; add only narrow helpers if tests show they reduce duplication.
- Modify `src/FFXIV Craft Architect.Core/Services/CommissionCostBasisResolver.cs`: prefer supported acquisition estimates from shopping plans, including vendor evidence, before aggregate market fallbacks.
- Modify `src/FFXIV Craft Architect.Web/Services/TradePayrollDraftModels.cs`: add assigned-crafter workflow metadata to generated payroll drafts only if the page needs a single composed view model.
- Create `src/FFXIV Craft Architect.Web/Services/TradePayrollPersistenceService.cs`: load/save payroll workflow drafts, find or create the draft for the current company/order/plan, and merge saved workflow choices onto regenerated material lines.
- Modify `src/FFXIV Craft Architect.Web/Services/TradeOperationsPersistenceService.cs`: expose payroll draft persistence by delegating to `IndexedDbService`, or keep a separate payroll persistence service if constructor churn stays cleaner.
- Modify `src/FFXIV Craft Architect.Web/Services/IndexedDbService.cs`: add typed JS interop wrappers for payroll draft CRUD.
- Modify `src/FFXIV Craft Architect.Web/wwwroot/indexedDB.js`: add `tradePayrollDrafts`, CRUD functions, diagnostics booleans, required-store checks, and repair-upgrade creation.
- Modify `src/FFXIV Craft Architect.Web/Pages/TradePayroll.razor`: replace commissioner field, load company/crafters/orders/payroll draft, auto-save responsibility and assigned-crafter changes, update copied text.
- Modify `src/FFXIV Craft Architect.Web/Program.cs`: register the new payroll persistence service.
- Modify tests under `src/FFXIV Craft Architect.Tests`: add workflow persistence contract tests, resolver tests for acquisition evidence, and markup tests for assigned crafter copy.

---

## Task 1: Add Durable Payroll Draft Model

**Files:**
- Create: `src/FFXIV Craft Architect.Core/Models/TradePayrollWorkflowModels.cs`
- Test: `src/FFXIV Craft Architect.Tests/TradePayrollWorkflowModelTests.cs`

- [ ] **Step 1: Write failing model tests**

Add tests that assert a new local payroll draft has:

- stable string `Id`
- `CompanyProfileId`
- optional `OrderId`
- `PlanSessionVersion`
- `SourcePlanName`
- nullable `AssignedCrafterId`
- per-line responsibility records keyed by `ItemId` and `RequiresHq`
- `RemoteId`
- `SyncState = TradeSyncState.LocalOnly`
- `CreatedAtUtc` and `UpdatedAtUtc`

Run:

```powershell
dotnet test 'src/FFXIV Craft Architect.Tests/FFXIV Craft Architect.Tests.csproj' --filter "FullyQualifiedName~TradePayrollWorkflowModelTests" -p:UseSharedCompilation=false
```

Expected: fail because the model does not exist.

- [ ] **Step 2: Add the model**

Create:

```csharp
namespace FFXIV_Craft_Architect.Core.Models;

public sealed class TradePayrollWorkflowDraft
{
    public string Id { get; set; } = Guid.NewGuid().ToString("D");
    public Guid CompanyProfileId { get; set; }
    public Guid? OrderId { get; set; }
    public long PlanSessionVersion { get; set; }
    public long MarketAnalysisVersion { get; set; }
    public string SourcePlanName { get; set; } = "Active craft plan";
    public Guid? AssignedCrafterId { get; set; }
    public string? AssignedCrafterDisplayName { get; set; }
    public decimal CommissionPercent { get; set; } = CommissionPayoutPolicy.Default.CommissionPercent;
    public IReadOnlyList<TradePayrollResponsibilityLine> Responsibilities { get; set; } = Array.Empty<TradePayrollResponsibilityLine>();
    public string? RemoteId { get; set; }
    public TradeSyncState SyncState { get; set; } = TradeSyncState.LocalOnly;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed record TradePayrollResponsibilityLine(
    int ItemId,
    bool RequiresHq,
    CommissionMaterialResponsibility Responsibility);
```

- [ ] **Step 3: Verify**

Run the focused test command again.

Expected: pass.

---

## Task 2: Persist Payroll Drafts In IndexedDB

**Files:**
- Modify: `src/FFXIV Craft Architect.Web/wwwroot/indexedDB.js`
- Modify: `src/FFXIV Craft Architect.Web/Services/IndexedDbService.cs`
- Modify: `src/FFXIV Craft Architect.Tests/TradeOperationsPersistenceContractTests.cs`
- Test: `src/FFXIV Craft Architect.Tests/TradeOperationsFailureHandlingTests.cs`

- [ ] **Step 1: Write failing persistence contract tests**

Extend contract tests to assert:

- `const STORE_TRADE_PAYROLL_DRAFTS = 'tradePayrollDrafts';`
- `saveTradePayrollDraft`
- `loadTradePayrollDrafts`
- `deleteTradePayrollDraft`
- `SaveTradePayrollDraftAsync`
- `LoadTradePayrollDraftsAsync`
- `DeleteTradePayrollDraftAsync`
- diagnostics include `HasPayrollDraftsStore`
- `IsReady` requires the payroll draft store

Run:

```powershell
dotnet test 'src/FFXIV Craft Architect.Tests/FFXIV Craft Architect.Tests.csproj' --filter "FullyQualifiedName~TradeOperationsPersistenceContractTests|FullyQualifiedName~TradeOperationsFailureHandlingTests" -p:UseSharedCompilation=false
```

Expected: fail on missing store/functions/properties.

- [ ] **Step 2: Update IndexedDB schema and repair path**

In `indexedDB.js`:

- bump `DB_VERSION` from `6` to `7`
- add `STORE_TRADE_PAYROLL_DRAFTS`
- create object store with `keyPath: 'id'`
- add indexes for `companyProfileId`, `orderId`, `planSessionVersion`, and `updatedAtUtc`
- include the store in `hasRequiredTradeStores(...)`
- include the store in `createTradeStoreDiagnostics(...)`
- create the store in both `onupgradeneeded` and `openTradeStoreRepairUpgrade(...)`
- add save/load/delete functions using the existing generic store helpers
- export the new functions from `window.IndexedDB`

- [ ] **Step 3: Update C# interop wrappers**

In `IndexedDbService.cs`, add:

```csharp
public async Task<bool> SaveTradePayrollDraftAsync(TradePayrollWorkflowDraft draft)
public async Task<List<TradePayrollWorkflowDraft>> LoadTradePayrollDraftsAsync(Guid companyProfileId)
public async Task<bool> DeleteTradePayrollDraftAsync(Guid draftId)
```

Load failures should throw `InvalidOperationException`, matching crafters/orders. Save/delete failures should return `false` and log.

- [ ] **Step 4: Verify**

Run the focused persistence tests again.

Expected: pass.

---

## Task 3: Create Payroll Workflow Persistence Service

**Files:**
- Create: `src/FFXIV Craft Architect.Web/Services/TradePayrollPersistenceService.cs`
- Modify: `src/FFXIV Craft Architect.Web/Program.cs`
- Test: `src/FFXIV Craft Architect.Tests/TradePayrollPersistenceServiceTests.cs`

- [ ] **Step 1: Write failing service tests**

Use a fake in-memory persistence dependency if direct `IndexedDbService` mocking is awkward. Cover:

- selecting an existing draft by `OrderId` first
- selecting an existing draft by `CompanyProfileId + PlanSessionVersion` when no order is linked
- creating a new local draft when no match exists
- merging saved responsibilities onto regenerated lines by `ItemId + RequiresHq`
- leaving new material lines at default `CommissionMaterialResponsibility.Crafter`

Current active procurement aggregation collapses demand by `ItemId` and ORs `RequiresHq`, so the first implementation should not try to preserve separate HQ and NQ responsibility rows for the same item. Keep `RequiresHq` in the persisted key as a compatibility guard, but expect one payroll line per item with the current pipeline.

Run:

```powershell
dotnet test 'src/FFXIV Craft Architect.Tests/FFXIV Craft Architect.Tests.csproj' --filter "FullyQualifiedName~TradePayrollPersistenceServiceTests" -p:UseSharedCompilation=false
```

Expected: fail because the service does not exist.

- [ ] **Step 2: Implement service**

The service should expose methods equivalent to:

```csharp
Task<TradePayrollWorkflowDraft> GetOrCreateDraftAsync(
    Guid companyProfileId,
    Guid? orderId,
    long planSessionVersion,
    long marketAnalysisVersion,
    string sourcePlanName,
    Guid? assignedCrafterId,
    string? assignedCrafterDisplayName);

Task<bool> SaveDraftAsync(TradePayrollWorkflowDraft draft);

IReadOnlyList<CommissionPayrollInputLine> ApplyResponsibilities(
    IReadOnlyList<CommissionPayrollInputLine> regeneratedLines,
    TradePayrollWorkflowDraft draft);
```

On save, update `UpdatedAtUtc`.

- [ ] **Step 3: Register service**

Add to `Program.cs`:

```csharp
builder.Services.AddScoped<TradePayrollPersistenceService>();
```

- [ ] **Step 4: Verify**

Run the focused service tests again.

Expected: pass.

---

## Task 4: Use Acquisition Evidence For Payroll Unit Cost

**Files:**
- Modify: `src/FFXIV Craft Architect.Core/Services/CommissionCostBasisResolver.cs`
- Modify: `src/FFXIV Craft Architect.Tests/CommissionCostBasisResolverTests.cs`

- [ ] **Step 1: Write failing resolver tests**

Add tests for:

- vendor recommendation uses vendor total scaled to requested quantity
- supported recommended world purchase uses acquisition total divided by quantity
- supported split purchase uses acquisition total divided by quantity and gets a split-specific explanation
- supported acquisition evidence wins over competitive market average
- unsupported projection does not become default eligible evidence; either falls through to market aggregate or uses an explicit warning if product chooses to show projected evidence later
- existing stale recommended-world warnings still appear

Run:

```powershell
dotnet test 'src/FFXIV Craft Architect.Tests/FFXIV Craft Architect.Tests.csproj' --filter "FullyQualifiedName~CommissionCostBasisResolverTests" -p:UseSharedCompilation=false
```

Expected: fail because the resolver still prioritizes market averages.

- [ ] **Step 2: Implement acquisition-first selection**

In `BuildLine(...)`, before `SelectUnitCost(analysis)`, check `plansByItemId`:

```csharp
var acquisition = MarketPurchaseCostProjectionService.Estimate(
    shoppingPlan,
    item.TotalQuantity,
    item.RequiresHq,
    includeVendor: true);
```

If `acquisition.IsDefaultEligible` and `acquisition.HasCost`, set:

- `unitCost = Math.Ceiling(acquisition.Cost / item.TotalQuantity)`
- `evidenceSource = "Acquisition recommendation"`
- vendor explanation when `shoppingPlan.RecommendedWorld?.WorldName == MarketShoppingConstants.VendorWorldName`; vendor estimates currently return `World = null`
- split explanation when `shoppingPlan.RecommendedSplit` has entries; split estimates can also return `World = null`
- world explanation when `acquisition.World` is a real world
- `evidenceTimestampUtc` from matching recommended-world upload time when present

Only use `SelectUnitCost(analysis)` when no supported acquisition estimate exists.

- [ ] **Step 3: Preserve warnings**

Keep the current recommendation-age warning behavior. If acquisition estimate is unavailable and market evidence is used, keep existing missing-scope and analysis-warning behavior.

- [ ] **Step 4: Verify**

Run resolver tests, then:

```powershell
dotnet test 'src/FFXIV Craft Architect.Tests/FFXIV Craft Architect.Tests.csproj' --filter "FullyQualifiedName~MarketPurchaseCostProjectionServiceTests|FullyQualifiedName~CommissionCostBasisResolverTests" -p:UseSharedCompilation=false
```

Expected: pass.

---

## Task 5: Replace Commissioner Field With Assigned Crafter Workflow

**Files:**
- Modify: `src/FFXIV Craft Architect.Web/Pages/TradePayroll.razor`
- Modify: `src/FFXIV Craft Architect.Web/Pages/TradePayroll.razor.css`
- Modify: `src/FFXIV Craft Architect.Tests/TradePayrollMarkupTests.cs`

- [ ] **Step 1: Write failing markup tests**

Update markup tests to assert:

- the page contains `Assigned Crafter`
- the page does not contain `Commissioner name`
- copied summary builder contains `Assigned crafter:`
- copied summary builder no longer contains `Name:`
- the page injects `TradeOperationsPersistenceService` and `TradePayrollPersistenceService`

Run:

```powershell
dotnet test 'src/FFXIV Craft Architect.Tests/FFXIV Craft Architect.Tests.csproj' --filter "FullyQualifiedName~TradePayrollMarkupTests" -p:UseSharedCompilation=false
```

Expected: fail on current commissioner field.

- [ ] **Step 2: Load company workflow state**

In `TradePayroll.razor`:

- inject `TradeOperationsPersistenceService`
- inject `TradePayrollPersistenceService`
- load active company profile
- load crafters for the company
- load active orders for the company
- find a matching order by `SourceSnapshot.PlanSessionVersion == AppState.PlanSessionVersion`
- use the matching order id when loading/creating payroll draft

- [ ] **Step 3: Replace UI field**

Replace the text field with a `MudSelect<Guid?>` labeled `Assigned Crafter`.

Options:

- empty value: `Unassigned`
- one option per `TradeCrafterProfile`, label from `DisplayName`

On change:

- update `_payrollWorkflowDraft.AssignedCrafterId`
- update `_payrollWorkflowDraft.AssignedCrafterDisplayName`
- save payroll draft
- leave `TradeOrder.AssignedCrafterId` unchanged in this pass; order assignment remains owned by the Orders page until a later tighter integration explicitly moves that workflow

- [ ] **Step 4: Update copy summary**

Change summary text from:

```text
Name: ...
```

to:

```text
Assigned crafter: ...
```

Use `Unassigned` when no crafter is selected.

- [ ] **Step 5: Verify**

Run payroll markup tests.

Expected: pass.

---

## Task 6: Persist Responsibility Across Page Switches And Refreshes

**Files:**
- Modify: `src/FFXIV Craft Architect.Web/Pages/TradePayroll.razor`
- Modify: `src/FFXIV Craft Architect.Tests/TradePayrollMarkupTests.cs`
- Test: `src/FFXIV Craft Architect.Tests/TradePayrollPersistenceServiceTests.cs`

- [ ] **Step 1: Add tests for save-on-change hooks**

Markup/source tests should assert:

- `SetResponsibility` calls a save helper
- save helper writes `TradePayrollResponsibilityLine` entries
- `CreateDraft` or its replacement merges saved responsibility before recalculating payroll

Run focused payroll tests.

Expected: fail until page saves workflow state.

- [ ] **Step 2: Update page flow**

Change page initialization to:

1. create the generated payroll source from the active craft plan
2. load/create payroll workflow draft
3. merge saved responsibilities into generated lines
4. calculate payroll from merged lines
5. render

`SetResponsibility(...)` should:

1. update the editor line
2. update draft responsibility list
3. save draft
4. recalculate payroll

- [ ] **Step 3: Preserve fresh evidence**

Do not persist `UnitCost`, `EvidenceSource`, or `UnitCostExplanation` in workflow state. Those come from the active plan/market evidence each time the page is opened.

- [ ] **Step 4: Verify**

Run:

```powershell
dotnet test 'src/FFXIV Craft Architect.Tests/FFXIV Craft Architect.Tests.csproj' --filter "FullyQualifiedName~TradePayroll" -p:UseSharedCompilation=false
```

Expected: pass.

---

## Task 7: Link Payroll Drafts Back To Orders

**Files:**
- Modify: `src/FFXIV Craft Architect.Web/Pages/TradePayroll.razor`
- Modify: `src/FFXIV Craft Architect.Web/Pages/TradeOrders.razor`
- Modify: `src/FFXIV Craft Architect.Tests/TradeOperationsFailureHandlingTests.cs`
- Modify: `src/FFXIV Craft Architect.Tests/TradeOrdersMarkupTests.cs`

- [ ] **Step 1: Add failing linkage tests**

Assert that:

- payroll page writes `TradeOrder.PayrollDraftId` when it creates or opens a draft for a matching order
- order history can receive `TradeOrderHistoryEventKind.PayrollLinked`
- repeated payroll opens do not add duplicate payroll-linked history entries

Run focused Trade operation tests.

Expected: fail until linkage exists.

- [ ] **Step 2: Implement order linkage**

When payroll draft is associated with an order:

- if `order.PayrollDraftId` is empty, set it to `draft.Id`
- if `order.PayrollDraftId` already equals `draft.Id`, do not add duplicate history
- add one `PayrollLinked` history event
- save the order

Do not add navigation or a full Orders-page payroll button unless it is a tiny, obvious affordance. This pass is workflow plumbing first.

- [ ] **Step 3: Verify**

Run:

```powershell
dotnet test 'src/FFXIV Craft Architect.Tests/FFXIV Craft Architect.Tests.csproj' --filter "FullyQualifiedName~TradeOrders|FullyQualifiedName~TradeOperations" -p:UseSharedCompilation=false
```

Expected: pass.

---

## Task 8: Full Verification And Manual Smoke

**Files:**
- No planned edits unless verification finds a real defect.

- [ ] **Step 1: Run focused tests**

```powershell
dotnet test 'src/FFXIV Craft Architect.Tests/FFXIV Craft Architect.Tests.csproj' --filter "FullyQualifiedName~TradePayroll|FullyQualifiedName~CommissionCostBasisResolver|FullyQualifiedName~MarketPurchaseCostProjectionService|FullyQualifiedName~TradeOperations" -p:UseSharedCompilation=false
```

Expected: pass.

- [ ] **Step 2: Run broader tests**

```powershell
dotnet test 'src/FFXIV Craft Architect.Tests/FFXIV Craft Architect.Tests.csproj' -p:UseSharedCompilation=false
```

Expected: pass.

- [ ] **Step 3: Build web app**

```powershell
dotnet build 'src/FFXIV Craft Architect.Web/FFXIV Craft Architect.Web.csproj' -p:UseSharedCompilation=false
```

Expected: success.

- [ ] **Step 4: Restart dev server**

Stop any existing `localhost:5000` process for this worktree, then run:

```powershell
dotnet run --project 'src/FFXIV Craft Architect.Web/FFXIV Craft Architect.Web.csproj' --urls http://localhost:5000
```

Expected: app serves from the current worktree.

- [ ] **Step 5: Manual smoke workflow**

In the browser:

- enable developer/debug tools if the payroll route remains guarded by `AppState.SecretDebugToolsEnabled`
- create or load a craft plan
- run market analysis so shopping plans exist
- create a Trade order from the active craft plan with an assigned crafter
- open Payroll
- confirm `Assigned Crafter` defaults from the order
- change one material responsibility
- switch to Orders and back
- refresh the page
- confirm assigned crafter and responsibility persist
- copy payroll summary and confirm it says `Assigned crafter`
- verify a vendor-priced item uses vendor cost in payroll when vendor acquisition is recommended

Expected: workflow state persists and payroll pricing matches acquisition evidence.

---

## Risks And Guardrails

- Do not persist generated market evidence in the workflow draft. Persisting prices would make payroll durable but stale; this feature needs durable workflow choices over fresh calculated evidence.
- Do not silently fall back when the payroll IndexedDB store is missing. Diagnostics must name the missing store, same as the existing Trade store hardening.
- Do not make assigned crafter a free-text field. It should come from the company roster.
- Do not globally key responsibility by item id alone if HQ/NQ lines can coexist in a later pipeline. The current active procurement path collapses by item id and ORs HQ state, so this pass should preserve the current one-line-per-item behavior and use `ItemId + RequiresHq` only as the persisted line key.
- Do not treat unsupported projected costs as supported acquisition evidence. The user asked for acquisition evidence, not averages wearing a better hat.

## Self-Review Notes

- Spec coverage: assigned crafter replacement is covered in Task 5; responsibility persistence is covered in Tasks 1, 2, 3, and 6; acquisition-evidence pricing including vendor items is covered in Task 4; workflow/order incorporation is covered in Task 7.
- Placeholder scan: no deferred-placeholder language is used.
- Type consistency: `TradePayrollWorkflowDraft`, `TradePayrollResponsibilityLine`, and IndexedDB method names are consistent across tasks.

## Agent Review Notes

- Vendor estimates from `MarketPurchaseCostProjectionService.Estimate(...)` return supported cost with `World = null`, so Task 4 now requires vendor detection from `shoppingPlan.RecommendedWorld`.
- Split purchase estimates can also return `World = null`, so Task 4 now requires split-specific explanation behavior.
- `TradeOrder.PayrollDraftId` is currently `string?`, so Task 1 models payroll draft ids as canonical strings rather than `Guid`.
- Current material aggregation already collapses item demand by `ItemId`, so the responsibility key guidance now reflects current one-line-per-item behavior.
- Manual smoke now calls out the current developer-mode guard on the payroll route.
