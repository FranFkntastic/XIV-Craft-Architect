# Browser truth suite

This suite tests an already-extracted Web publish. It never reads product source or runs
`dotnet publish`.

Point `--web-root` at the published static root containing `index.html`, `indexedDB.js`,
`appsettings.json`, and `_framework/blazor.webassembly.js`. Point `--output` at the terminal
JSON report. Both paths are required:

Runner also requires `TRUTHFUL_RUN_ID`, `TRUTHFUL_RUN_ATTEMPT`, `TRUTHFUL_SOURCE_SHA`,
`TRUTHFUL_ARTIFACT_SHA`, `TRUTHFUL_HARNESS_SHA`, and `TRUTHFUL_FIXTURE_SHA`. Artifact
workflow supplies these identities; guessed or omitted identities fail closed.

```powershell
npm test -- --web-root "C:\artifacts\craft-architect\wwwroot" --output "C:\tmp\ca-browser-truth.json"
```

The runner executes six isolated scenarios: fresh/current IndexedDB, historical v3 schema
upgrade, and production procurement kill-switch flow in Chromium and Firefox. Product flow
uses `fixtures/browser/truth-product.craftplan` plus deterministic in-process Garland and
Universalis responses. Every request is intercepted; only runner origin reaches a socket,
known fixture endpoints are fulfilled in-process, and every other request is rejected.

Product success requires visible native import, explicit market analysis, name-first item
search and selection, manual acquisition choice, navigation, final reload restoration, and
disabled procurement controls. It also requires no route-execution lifecycle evidence and no
Workshop Host acquisition request. Missing fixtures or current product affordances are
reported as blockers and fail the run; no assertion is skipped.

Runner writes output once, after browser diagnostics and final reload, then emits the same
single JSON document to stdout. Missing assertions, operation deadlines, browser diagnostics,
or cleanup failures produce a nonzero exit.

`crasher-verify.mjs` and `workflow-oracle.mjs` remain diagnostic benchmark tooling. They are
intentionally outside `npm test` and are not correctness evidence while route generation is
disabled.

## Hosted order board acceptance

`hosted-order-board.mjs` is a focused Chromium acceptance probe for the last-known order
board. It seeds 100 active hosted commissions across every real attention group, holds owner
verification open, and proves that classification, selection, collapse, and scroll state stay
stable. It then releases one background response and proves a selected-order lifecycle demand
runs first in the queued page, opens one dialog, and does not duplicate that identity.

```powershell
npm run test:hosted-order-board -- --web-root "C:\artifacts\craft-architect\wwwroot" --output "C:\tmp\hosted-order-board.json" --screenshot "C:\tmp\hosted-order-board.png"
```

The same deterministic fixture records exact request count, serialized request and response
bytes, and wall time for the singleton baseline or the batch implementation. Run each mode
against an immutable published source tree; `baseline` requires 100 paced singleton requests,
while `feature` requires two 50-item comparison batches, 100 unique identities, clean browser
diagnostics, and no more than 4,079.271 ms wall time.

```powershell
npm run test:hosted-order-board -- --web-root "C:\artifacts\craft-architect\wwwroot" --output "C:\tmp\hosted-owner-baseline.json" --benchmark baseline
npm run test:hosted-order-board -- --web-root "C:\artifacts\craft-architect\wwwroot" --output "C:\tmp\hosted-owner-feature.json" --benchmark feature
```
