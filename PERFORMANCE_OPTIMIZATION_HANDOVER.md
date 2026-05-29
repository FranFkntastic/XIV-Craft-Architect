# FFXIV Craft Architect Performance and Acquisition Handover

## Current branch

- Branch: `codex/acquisition-decision-consolidation`
- Base at start of this slice: `95dce40 Stop tracking local workspace metadata`
- Goal: consolidate acquisition-source decisions, reduce unnecessary app-wide invalidation, and measure whether the first performance slice improved large-plan responsiveness.

## Changes made

### Acquisition decision consolidation

- Added source-reason/provenance state to plan nodes so the app can distinguish restored, user-selected, defaulted, coerced, and availability-required acquisition choices.
- Centralized acquisition-source validation and assignment through `AcquisitionPlanningService`.
- Added reconciliation that updates source decisions when market evidence changes, while preserving user-selected decisions when still valid.
- Moved cost calculation toward evidence-backed shopping-plan data instead of raw node prices where market analysis already has better evidence.
- Updated affected UI flows in Recipe Planner, Market Analysis, Acquisition Evaluation, Procurement Plan, and the acquisition details panel to call the consolidated service paths.

### AppState notification and autosave plumbing

- Added scoped AppState change metadata and version counters for plan structure, plan decisions, plan prices, market analysis, procurement overlay, settings, and status.
- Added batched state-change publication so restore/market-update paths can emit one combined change instead of several immediate events.
- Preserved the existing broad `OnPlanChanged` and `OnShoppingListChanged` events for compatibility.
- Added dirty persisted-state buckets and autosave leases so clean timer ticks can skip serialization/writes.
- Changed timer autosave to skip when a save is already in flight; explicit/final saves still wait.
- Added snapshot metrics for stored plans to quantify plan/core and market-analysis payload sizes.
- Updated IndexedDB writes to resolve when the transaction completes rather than when the individual request is queued.

### Tests added

- `AppStatePerformanceStateTests`
- `IndexedDbServiceAutoSaveTests`
- `StoredPlanSnapshotMetricsTests`

Existing acquisition tests were expanded for source reconciliation, user-choice preservation, source availability, and evidence-aware costs.

## Measured impact

### Synthetic autosave persistence benchmark

The synthetic browser benchmark used a large autosave payload because no large representative `.craftplan` fixture was available at the time.

- Seeded fixture: 500 project items, 500 market plans, 500 market analyses.
- Initial persisted payload: about 9.1 MB.
- Saved payload after restore: about 11.6 MB.
- Observation window: 95 seconds, crossing multiple 30-second autosave ticks.

Result:

- Current branch: 1 autosave write, about 11.6 MB written.
- Baseline `main`: 2 autosave writes, about 23.2 MB written.
- Individual write timings were similar, roughly 75-95 ms.

Conclusion: this slice reduces repeated idle persistence/write pressure. It does not make an individual IndexedDB write meaningfully faster.

### Real craftplan UI responsiveness probe

After a real craftplan was provided at `C:\Users\gianf\Downloads\4x Whale pressure hulls.craftplan`, a lightweight Playwright probe compared current branch against `main`.

Plan shape:

- File size: about 68 KB.
- Nodes: 75.
- Craftable nodes: 23.
- Child edges: 74.
- Distinct item IDs: 52.

Representative action timings were effectively tied:

- Import craftplan: current about 1343 ms; baseline about 1400 ms.
- Switch to Market Analysis: both about 900-940 ms.
- Switch to Acquisition Evaluation: both about 880-890 ms.
- Switch to Procurement Plan: both about 860-870 ms.
- Return to Recipe Planner: both about 840-850 ms.

Conclusion: this slice does not produce a meaningful visible UI responsiveness gain for the supplied craftplan. The autosave plumbing is useful foundation, but render/projection work remains the likely source of visible friction.

## Proposed next optimization work

### 1. Consume scoped AppState changes in UI pages

The scoped `OnStateChanged` event exists, but most pages still subscribe to broad legacy events:

- `Pages/Index.razor`
- `Pages/MarketAnalysis.razor`
- `Pages/AcquisitionEvaluation.razor`
- `Pages/ProcurementPlan.razor`
- `Shared/StatusBar.razor`

Next step: migrate these pages/components to react only to the scopes they actually display. Keep broad events temporarily for compatibility, but stop relying on them in hot UI surfaces.

Expected impact: fewer unnecessary `StateHasChanged` calls and fewer full-page re-renders after market/procurement/status-only changes.

### 2. Cache Acquisition Evaluation decision rows

`AcquisitionEvaluation.razor` currently builds decision rows during render from mutable plan state, then computes aggregate source/cost/usages from those rows.

Next step: introduce a cached decision-row view model keyed by plan structure, decision, price, and market-analysis versions. Recompute only when one of those scopes changes.

Expected impact: faster tab activation and source-toggle response, especially with larger plans or repeated node occurrences.

### 3. Cache market-analysis projections

`MarketAnalysisListPanel.razor` repeatedly looks up `MarketItemAnalyses.FirstOrDefault(...)` for row helpers and sort helpers.

Next step: build a per-item analysis dictionary or row view model whenever market-analysis state changes. Use that for coverage, recommended world, rank, cost, and sort values.

Expected impact: lower render cost for market grids and details, especially after region-wide analysis produces many item/world rows.

### 4. Cache procurement route cards and totals

`ProcurementRouteTreePanel.razor` calls route-card, total-cost, warning, and error helpers from render.

Next step: cache the procurement route card model by procurement-overlay and market-analysis versions. Keep expand/collapse state local, but avoid rebuilding route grouping on every render.

Expected impact: smoother procurement tab activation and world expand/collapse behavior once shopping plans are populated.

### 5. Move render-time cost display into cached display values

`RecipeNodeView.razor` no longer owns recursive craft-cost calculation, but it still asks `AcquisitionPlanningService.TryGetAcquisitionCost(...)` while rendering.

Next step: precompute per-node display cost/value text for the current plan and shopping evidence. Refresh on plan decision, plan price, and market-analysis changes.

Expected impact: lower recursive tree render cost and less repeated service work across identical node renders.

### 6. Add a durable lightweight responsiveness probe

Do not build a heavy benchmark suite yet. A small, opt-in dev-only probe is enough:

- Import/load a known `.craftplan`.
- Time tab switches, source toggles, and route/card expansion.
- Capture max frame gap and Long Task API counts.
- Emit JSON to the console or a dev-only diagnostics panel.

Expected impact: fast feedback while changing render/projection code without adding a permanent test harness burden.

## Cautions

- The autosave benchmark and UI probe measure different things. Do not use autosave write reduction as proof of interactive responsiveness.
- The supplied craftplan is useful, but it is not a large persistence payload. If future slow cases involve populated market analyses or procurement routes, keep a second fixture that preserves those states.
- The scoped AppState model is only valuable once hot pages actually consume scoped changes.
- Avoid broad UI redesign while optimizing; this should stay focused on reducing recomputation and render fanout.
