# Trade Orders Reconstruction Spec

Date: 2026-06-19
Branch: company-profile-work

## Goal

Make Orders the primary Trade Architect operational surface while preserving the current durable company, crafter, order, payroll, and craft-plan workflow model.

The current page is functional, but its information architecture still behaves like a wide order table with a right-side detail drawer. The mockup target is a durable operations board: order navigation, selected order work, and payment/procurement state should all be visible at once on a normal desktop viewport.

## Mockup Target

The target layout is a three-column board:

- Left rail: compact order navigator with search, grouped lifecycle sections, counts, selected-row highlight, and small status chips.
- Center workspace: selected or new order editing surface with title, assigned crafter, requested outputs, Craft Architect pipeline, and plan snapshot controls.
- Right rail: operational tabs for Payment, Procurement, and History. Payment is the default tab and starts with large payment/procurement totals and copy actions.
- Bottom status bar: company, region, data center, and active counts remain out of the main workflow.

The board should answer the daily operations questions without page-level scrolling:

- What order am I looking at?
- Who is responsible for it?
- What needs to happen next?
- What gets paid or procured?
- What has already happened?

## Layout Rules

Use a viewport-height, three-column CSS grid inside the existing app chrome:

- Left rail: 260-300 px.
- Center workspace: `minmax(620px, 1fr)`.
- Right rail: `clamp(420px, 30vw, 620px)`.

At desktop widths, avoid page-level scrolling for the core workflow. Internal scrolling is acceptable when content exceeds available space, with these priorities:

- Left rail may scroll freely.
- Right tab content may scroll, but payment totals and copy actions should remain visible at the top of the Payment tab.
- Center should keep the selected order header, requested outputs, pipeline, and snapshot controls visible at 1920x1080 with 90% browser zoom whenever the order is not unusually large.

Add a responsive fallback before implementation:

- Below the desktop breakpoint, keep the center workspace primary.
- Preserve the left navigator if possible.
- Convert the right operational panel into a drawer or lower tab region instead of squeezing payment tables until they become unreadable.

## Left Rail

The left rail replaces the current full-width grouped order tables.

Required behavior:

- Search input pinned at the top.
- New Order button pinned near search.
- Lifecycle groups with collapse controls and count chips.
- Archive collapsed by default.
- Compact order rows with selected highlight.
- Each row should expose enough scan context to avoid excessive clicking:
  - order title
  - assigned crafter or unassigned state
  - commissioned date or relative age
  - lifecycle/payment status chip

Selection must persist across:

- edits
- saves
- search changes
- group collapse/expand
- refresh/reload
- deep links using `orderId`
- build/refresh/open craft-plan operations

After a new order is created, it should immediately become selected in the rail and load into the center/right workspace.

## Center Workspace

The center column is the main workbench, not a stack of detail cards.

Modes:

- No selected order: show a clearly labeled New Order workspace.
- Selected order: show the selected order workspace.

The New Order and Selected Order modes may share structure, but the mode must be visually clear so users do not confuse creating a new order with editing an existing one.

Pinned or top-priority content:

- workspace title/mode
- Save Order action for selected orders
- order title
- assigned crafter
- requested outputs

Selected order workspace content:

- editable order title
- assigned crafter selector
- requested outputs table
- Craft Architect Pipeline section
- plan snapshot/open/build controls

Manual requested-output creation should move from the current top page panel into this center workspace.

## Craft Architect Pipeline

The pipeline is a workflow state strip, not decoration. Each stage should show state and the next action using conservative wording.

Recommended stages:

1. Order request
   - Complete: requested outputs exist.
   - Actionable: outputs missing or need editing.
   - Suggested labels: `Outputs captured`, `Add requested outputs`.

2. Recipe plan
   - Complete: order has a buildable/openable craft plan.
   - Warning/actionable: plan missing or stale.
   - Suggested labels: `Recipe plan built`, `Craft plan missing`, `Build craft plan`, `Open craft plan`.

3. Procurement evidence
   - Complete: material evidence exists.
   - Warning/actionable: evidence missing, stale, or needs review.
   - Avoid implying human approval when the app only knows evidence exists.
   - Suggested labels: `Evidence available`, `Needs review`, `Refresh analysis`.

4. Settlement
   - Complete/actionable based on payment readiness and order status.
   - Suggested labels: `Payment ready`, `Awaiting payment`, `In progress`, `Awaiting delivery`.

## Right Operational Panel

Use tabs:

- Payment
- Procurement
- History

Default tab for selected orders: Payment.

The right panel should not force payment, procurement, history, and closing controls into one vertical scroll stack.

### Payment Tab

Payment tab priority:

1. Large Payment amount with copy button.
2. Large Total estimated procurement with copy button.
3. Material responsibility table.
4. Payment summary copy action.
5. Status/payment lifecycle controls.
6. Destructive close/cancel controls, visually separated lower in the tab.

Payment copy actions are frequent and safe. Closing/canceling is lifecycle-changing and must remain harder to hit accidentally.

### Procurement Tab

Shows evidence and procurement details without crowding payment:

- material lines
- quantity
- unit cost
- estimated total
- responsibility
- evidence source
- warning details
- stale/missing evidence state

### History Tab

Shows:

- manual note input
- order history
- latest event preview or visible tab indicator when useful

History risks becoming invisible inside a tab, so a small latest-history cue should be considered in the right panel header or tab label.

## Mutation Boundaries

Keep mutation locations predictable:

- Title, assigned crafter, and requested outputs: center workspace.
- Material responsibility: Payment/Procurement table.
- Status/payment lifecycle controls: right operational panel.
- Close/cancel/reopen: right panel, confirmation-gated and visually separated from copy/payment actions.

Do not make status changes from the left rail.

## Non-Goals For This Pass

- Do not redesign the durable storage model unless a layout requirement exposes a real persistence bug.
- Do not rebuild the recipe pipeline internals.
- Do not remove the legacy Payroll route until Orders contains all required operational behavior and has been exercised.
- Do not introduce hosted sync or account flows.

## Acceptance Checks

At 1920x1080 with 90% browser zoom, selecting an assigned order should show all of the following without page-level scrolling:

- left rail with selected group and selected order row
- center order title
- center assigned crafter
- requested outputs
- Craft Architect pipeline
- plan snapshot/open/build controls
- right Payment tab selected
- payment amount
- total estimated procurement
- material responsibility summary/table

Operational checks:

- Creating a new order selects it immediately.
- Selection persists after save, payment responsibility edits, craft-plan build/open, search changes, and reload/deep link.
- Payment amount copy and procurement amount copy are always easy to reach on the Payment tab.
- Closing/canceling still requires confirmation and is visually separated from common copy actions.
- The page has an intentional narrower-width fallback rather than squeezed unreadable tables.

## Review Notes Incorporated

Mockup parity review:

- The plan should avoid simply rearranging the current page into three columns.
- Center density must be explicit.
- Payment should be the default right-side tab.
- Pipeline stages need concrete state mapping.
- Selection behavior must be preserved across operations.

User-friendliness review:

- Define responsive fallback before implementation.
- Pin the most frequent actions and values.
- Keep New Order and Selected Order modes visually distinct.
- Use conservative state wording.
- Keep destructive status controls separated from payment copy actions.
- Preserve enough scan context in the left rail.
