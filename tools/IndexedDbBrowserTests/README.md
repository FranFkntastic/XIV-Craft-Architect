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
